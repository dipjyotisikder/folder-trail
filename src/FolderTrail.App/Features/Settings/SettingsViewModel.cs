using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderTrail.App.Features.WatchedFolders;
using FolderTrail.App.Shared;
using FolderTrail.App.Shared.Dialogs;
using FolderTrail.App.Shared.Platform;
using FolderTrail.Core.Features.DataReset;
using FolderTrail.Core.Features.Diagnostics;
using FolderTrail.Core.Features.Retention;
using FolderTrail.Core.Features.Settings;
using FolderTrail.Core.Features.WatchedFolders;
using FolderTrail.Core.SharedKernel;
using FolderTrail.Core.SharedKernel.Diagnostics;

namespace FolderTrail.App.Features.Settings;

/// <summary>Preferences apply as soon as they change; there is no separate Save step.</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly WatchedFolderService _folders;
    private readonly RetentionService _retention;
    private readonly DataResetService _reset;
    private readonly IDialogService _dialogs;
    private readonly IShellLauncher _shell;
    private readonly INotifier _notifier;
    private readonly IUiDispatcher _ui;
    private readonly DiagnosticLog _log;
    private bool _loading;

    public SettingsViewModel(
        SettingsService settings,
        WatchedFolderService folders,
        RetentionService retention,
        DataResetService reset,
        AppDataLocation location,
        IDialogService dialogs,
        IShellLauncher shell,
        INotifier notifier,
        IUiDispatcher ui,
        DiagnosticLog log)
    {
        _settings = settings;
        _folders = folders;
        _retention = retention;
        _reset = reset;
        _dialogs = dialogs;
        _shell = shell;
        _notifier = notifier;
        _ui = ui;
        _log = log;
        DataDirectory = location.RootDirectory;
        SelectedTheme = ThemeOptions[0];
        SelectedRetention = RetentionOptions[1];
        SelectedCoalescing = CoalescingOptions[1];
        SelectedPeriodicScan = PeriodicScanOptions[2];
        Load(settings.Current);
        settings.Changed += (_, updated) => _ui.Post(() => Load(updated));
    }

    public IReadOnlyList<Option<ThemePreference>> ThemeOptions { get; } =
    [
        new(ThemePreference.System, "Use Windows setting"),
        new(ThemePreference.Light, "Light"),
        new(ThemePreference.Dark, "Dark"),
    ];

    public IReadOnlyList<Option<int?>> RetentionOptions { get; } =
    [
        new(30, "30 days"),
        new(90, "90 days (default)"),
        new(180, "180 days"),
        new(365, "1 year"),
        new(null, "Unlimited"),
    ];

    public IReadOnlyList<Option<int>> CoalescingOptions { get; } =
    [
        new(500, "0.5 seconds"),
        new(1000, "1 second (default)"),
        new(2000, "2 seconds"),
        new(5000, "5 seconds"),
    ];

    public IReadOnlyList<Option<int>> PeriodicScanOptions { get; } =
    [
        new(15, "Every 15 minutes"),
        new(30, "Every 30 minutes"),
        new(60, "Every hour (default)"),
        new(120, "Every 2 hours"),
        new(240, "Every 4 hours"),
    ];

    public string DataDirectory { get; }

    public string AppVersion => DiagnosticsService.AppVersion;

    public bool StartWithWindowsSupported => _settings.StartWithWindowsSupported;

    [ObservableProperty]
    public partial Option<ThemePreference>? SelectedTheme { get; set; }

    [ObservableProperty]
    public partial Option<int?>? SelectedRetention { get; set; }

    [ObservableProperty]
    public partial Option<int>? SelectedCoalescing { get; set; }

    [ObservableProperty]
    public partial Option<int>? SelectedPeriodicScan { get; set; }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial bool NotifyOnProblems { get; set; }

    [ObservableProperty]
    public partial bool IsDeleting { get; private set; }

    partial void OnSelectedThemeChanged(Option<ThemePreference>? value) => SaveIfChosen(value, s => s with { Theme = value!.Value });

    partial void OnSelectedRetentionChanged(Option<int?>? value) => SaveIfChosen(value, s => s with { RetentionDays = value!.Value });

    partial void OnSelectedCoalescingChanged(Option<int>? value) => SaveIfChosen(value, s => s with { CoalescingWindowMs = value!.Value });

    partial void OnSelectedPeriodicScanChanged(Option<int>? value) => SaveIfChosen(value, s => s with { PeriodicScanMinutes = value!.Value });

    partial void OnCloseToTrayChanged(bool value) => Save(s => s with { CloseToTray = value });

    partial void OnStartWithWindowsChanged(bool value) => Save(s => s with { StartWithWindows = value });

    partial void OnNotifyOnProblemsChanged(bool value) => Save(s => s with { NotifyOnMonitoringProblems = value });

    [RelayCommand]
    private async Task EditGlobalExclusions()
    {
        try
        {
            var rules = await _folders.GetExclusionsAsync(null);
            var dialog = new ExclusionsDialogViewModel(
                "Global exclusions",
                "These apply to every watched folder. The suggested defaults cover common noise such as version-control data, build output and temporary files; you can disable or remove any of them.",
                rules,
                isGlobal: true,
                inheritedPatterns: []);
            var edit = await _dialogs.ShowAsync<ExclusionEdit>(dialog);
            if (edit is not null)
            {
                await _folders.SetExclusionsAsync(null, edit.Rules);
                _notifier.Notify("Exclusions saved", "Watched folders are being rescanned so their inventory matches the new exclusions.", NoticeKind.Success);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Error("UI", "Saving global exclusions failed.", exception);
            _notifier.Notify("Couldn't save exclusions", exception.Message, NoticeKind.Error);
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        if (_shell.DirectoryExists(DataDirectory))
        {
            _shell.Open(DataDirectory);
        }
    }

    [RelayCommand]
    private async Task CleanUpNow()
    {
        try
        {
            var result = await _retention.PruneAsync();
            _notifier.Notify(
                "History cleaned up",
                result.EventsDeleted == 0 ? "Nothing was older than the retention period." : $"Deleted {Format.Count(result.EventsDeleted, "record", "records")} older than the retention period.",
                NoticeKind.Success);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Error("UI", "Cleaning up history failed.", exception);
            _notifier.Notify("Clean-up failed", exception.Message, NoticeKind.Error);
        }
    }

    [RelayCommand]
    private async Task DeleteAllData()
    {
        var confirmed = await _dialogs.ShowAsync<bool>(new ConfirmDialogViewModel(
            "Delete all FolderTrail data?",
            "This stops monitoring and permanently deletes the list of watched folders, all recorded activity, the inventory, monitoring gaps, exclusions, settings and diagnostic logs. It also removes FolderTrail from Windows startup. Your own files and folders are not touched.",
            "Delete all data",
            isDestructive: true,
            acknowledgement: "I understand this can't be undone."));
        if (!confirmed)
        {
            return;
        }

        IsDeleting = true;
        try
        {
            await _reset.DeleteAllDataAsync();
            _notifier.Notify("All data deleted", "FolderTrail is back to its first-run state.", NoticeKind.Success);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Error("UI", "Deleting all data failed.", exception);
            _notifier.Notify("Couldn't delete all data", exception.Message, NoticeKind.Error);
        }
        finally
        {
            IsDeleting = false;
        }
    }

    private void Load(AppSettings settings)
    {
        _loading = true;
        try
        {
            SelectedTheme = ThemeOptions.First(o => o.Value == settings.Theme);
            SelectedRetention = RetentionOptions.FirstOrDefault(o => o.Value == settings.RetentionDays) ?? RetentionOptions[1];
            SelectedCoalescing = CoalescingOptions.FirstOrDefault(o => o.Value == settings.CoalescingWindowMs) ?? CoalescingOptions[1];
            SelectedPeriodicScan = PeriodicScanOptions.FirstOrDefault(o => o.Value == settings.PeriodicScanMinutes) ?? PeriodicScanOptions[2];
            CloseToTray = settings.CloseToTray;
            StartWithWindows = settings.StartWithWindows;
            NotifyOnProblems = settings.NotifyOnMonitoringProblems;
        }
        finally
        {
            _loading = false;
        }
    }

    private void SaveIfChosen<T>(Option<T>? option, Func<AppSettings, AppSettings> change)
    {
        if (option is null)
        {
            // A combo box can clear its selection transiently; restore what's stored instead of saving.
            if (!_loading)
            {
                _ui.Post(() => Load(_settings.Current));
            }

            return;
        }

        Save(change);
    }

    private async void Save(Func<AppSettings, AppSettings> change)
    {
        if (_loading)
        {
            return;
        }

        try
        {
            await _settings.UpdateAsync(change);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Error("Settings", "Saving a setting failed.", exception);
            _notifier.Notify("Couldn't save the setting", exception.Message, NoticeKind.Error);
            Load(_settings.Current);
        }
    }
}
