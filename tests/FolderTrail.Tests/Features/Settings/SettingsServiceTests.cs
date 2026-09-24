using FolderTrail.Core.Features.Settings;
using FolderTrail.Core.SharedKernel.Persistence;
using FolderTrail.Tests.Support;

namespace FolderTrail.Tests.Features.Settings;

public sealed class SettingsServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Defaults_are_conservative()
    {
        using var harness = new CoreHarness();
        var settings = await harness.Settings.LoadAsync(Token);

        Assert.Equal(90, settings.RetentionDays);
        Assert.False(settings.CloseToTray);
        Assert.False(settings.StartWithWindows);
        Assert.True(settings.NotifyOnMonitoringProblems);
        Assert.False(settings.OnboardingCompleted);
        Assert.Equal(ThemePreference.System, settings.Theme);
        Assert.Equal(60, settings.PeriodicScanMinutes);
    }

    [Fact]
    public async Task Changes_persist_and_reload()
    {
        using var harness = new CoreHarness();
        await harness.Settings.UpdateAsync(
            s => s with { RetentionDays = null, CloseToTray = true, Theme = ThemePreference.Dark, CoalescingWindowMs = 2000, PeriodicScanMinutes = 240, OnboardingCompleted = true },
            Token);

        var reloaded = await new SettingsService(new KeyValueStore(harness.Database), harness.Startup).LoadAsync(Token);

        Assert.Null(reloaded.RetentionDays);
        Assert.True(reloaded.CloseToTray);
        Assert.Equal(ThemePreference.Dark, reloaded.Theme);
        Assert.Equal(2000, reloaded.CoalescingWindowMs);
        Assert.Equal(240, reloaded.PeriodicScanMinutes);
        Assert.True(reloaded.OnboardingCompleted);
    }

    [Fact]
    public async Task Values_outside_the_offered_choices_fall_back_to_defaults()
    {
        using var harness = new CoreHarness();
        var saved = await harness.Settings.UpdateAsync(s => s with { RetentionDays = 7, CoalescingWindowMs = 3, PeriodicScanMinutes = 1 }, Token);

        Assert.Equal(90, saved.RetentionDays);
        Assert.Equal(1000, saved.CoalescingWindowMs);
        Assert.Equal(60, saved.PeriodicScanMinutes);
    }

    [Fact]
    public async Task Start_with_windows_toggles_the_registration_and_disabling_removes_it()
    {
        using var harness = new CoreHarness();
        await harness.Settings.UpdateAsync(s => s with { StartWithWindows = true }, Token);
        Assert.True(harness.Startup.Enabled);
        Assert.True(harness.Settings.Current.StartWithWindows);

        await harness.Settings.UpdateAsync(s => s with { StartWithWindows = false }, Token);
        Assert.False(harness.Startup.Enabled);
        Assert.False((await harness.Settings.LoadAsync(Token)).StartWithWindows);
    }

    [Fact]
    public async Task Changes_are_announced_once_per_actual_change()
    {
        using var harness = new CoreHarness();
        var raised = 0;
        harness.Settings.Changed += (_, _) => raised++;

        await harness.Settings.UpdateAsync(s => s with { CloseToTray = true }, Token);
        await harness.Settings.UpdateAsync(s => s with { CloseToTray = true }, Token);

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Windows_startup_command_quotes_the_path_and_starts_minimized()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var registration = new WindowsStartupRegistration(@"C:\Program Files\FolderTrail\FolderTrail.exe");
        Assert.Equal("\"C:\\Program Files\\FolderTrail\\FolderTrail.exe\" --minimized", registration.Command);
    }
}
