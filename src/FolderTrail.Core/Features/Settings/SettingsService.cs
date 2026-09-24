using System.Globalization;
using System.Security;
using FolderTrail.Core.SharedKernel.Persistence;

namespace FolderTrail.Core.Features.Settings;

public sealed class SettingsService(KeyValueStore store, IStartupRegistration startup)
{
    private const string RetentionKey = "retention.days";
    private const string CloseToTrayKey = "window.closeToTray";
    private const string ThemeKey = "appearance.theme";
    private const string CoalescingKey = "monitoring.coalescingMs";
    private const string PeriodicScanKey = "monitoring.periodicScanMinutes";
    private const string NotifyKey = "notifications.monitoringProblems";
    private const string OnboardingKey = "onboarding.completed";
    private const string Unlimited = "unlimited";

    private AppSettings _current = AppSettings.Defaults;

    public event EventHandler<AppSettings>? Changed;

    public AppSettings Current => Volatile.Read(ref _current);

    public bool StartWithWindowsSupported => startup.IsSupported;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var values = await store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var defaults = AppSettings.Defaults;
        var loaded = new AppSettings
        {
            RetentionDays = values.TryGetValue(RetentionKey, out var retention)
                ? retention == Unlimited ? null : ParseInt(retention, defaults.RetentionDays ?? 90)
                : defaults.RetentionDays,
            CloseToTray = ParseBool(values, CloseToTrayKey, defaults.CloseToTray),
            Theme = values.TryGetValue(ThemeKey, out var theme) && Enum.TryParse<ThemePreference>(theme, out var parsedTheme) ? parsedTheme : defaults.Theme,
            CoalescingWindowMs = values.TryGetValue(CoalescingKey, out var coalescing) ? ParseInt(coalescing, defaults.CoalescingWindowMs) : defaults.CoalescingWindowMs,
            PeriodicScanMinutes = values.TryGetValue(PeriodicScanKey, out var periodic) ? ParseInt(periodic, defaults.PeriodicScanMinutes) : defaults.PeriodicScanMinutes,
            NotifyOnMonitoringProblems = ParseBool(values, NotifyKey, defaults.NotifyOnMonitoringProblems),
            OnboardingCompleted = ParseBool(values, OnboardingKey, defaults.OnboardingCompleted),
            StartWithWindows = ReadStartupState(),
        }.Normalized();

        Volatile.Write(ref _current, loaded);
        Changed?.Invoke(this, loaded);
        return loaded;
    }

    /// <exception cref="InvalidOperationException">The Start with Windows entry couldn't be changed.</exception>
    public async Task<AppSettings> UpdateAsync(AppSettings updated, CancellationToken cancellationToken = default)
    {
        var next = updated.Normalized();
        var previous = Current;
        if (next.StartWithWindows != ReadStartupState())
        {
            try
            {
                startup.SetEnabled(next.StartWithWindows);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException or PlatformNotSupportedException)
            {
                throw new InvalidOperationException("FolderTrail couldn't change the Start with Windows setting: " + exception.Message, exception);
            }
        }

        await store.SetManyAsync(
                new Dictionary<string, string?>
                {
                    [RetentionKey] = next.RetentionDays?.ToString(CultureInfo.InvariantCulture) ?? Unlimited,
                    [CloseToTrayKey] = Format(next.CloseToTray),
                    [ThemeKey] = next.Theme.ToString(),
                    [CoalescingKey] = next.CoalescingWindowMs.ToString(CultureInfo.InvariantCulture),
                    [PeriodicScanKey] = next.PeriodicScanMinutes.ToString(CultureInfo.InvariantCulture),
                    [NotifyKey] = Format(next.NotifyOnMonitoringProblems),
                    [OnboardingKey] = Format(next.OnboardingCompleted),
                },
                cancellationToken)
            .ConfigureAwait(false);

        next = next with { StartWithWindows = ReadStartupState() };
        Volatile.Write(ref _current, next);
        if (next != previous)
        {
            Changed?.Invoke(this, next);
        }

        return next;
    }

    public Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken cancellationToken = default) =>
        UpdateAsync(change(Current), cancellationToken);

    private bool ReadStartupState()
    {
        try
        {
            return startup.IsEnabled();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
        {
            return false;
        }
    }

    private static int ParseInt(string value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static bool ParseBool(IReadOnlyDictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var value) ? value == "1" : fallback;

    private static string Format(bool value) => value ? "1" : "0";
}
