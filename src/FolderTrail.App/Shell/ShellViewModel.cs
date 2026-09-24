using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderTrail.App.Features.Diagnostics;
using FolderTrail.App.Features.Onboarding;
using FolderTrail.App.Features.Settings;
using FolderTrail.App.Features.Timeline;
using FolderTrail.App.Features.WatchedFolders;
using FolderTrail.App.Shared;
using FolderTrail.App.Shared.Dialogs;
using FolderTrail.App.Shared.Platform;
using FolderTrail.Core.Features.DataReset;
using FolderTrail.Core.Features.Monitoring;
using FolderTrail.Core.Features.Settings;

namespace FolderTrail.App.Shell;

public enum ShellPage
{
    Activity,
    Settings,
    Diagnostics,
}

/// <summary>Composes the feature slices into the main window and owns navigation and alerts.</summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    private static readonly TimeSpan AlertCooldown = TimeSpan.FromMinutes(10);

    private readonly SettingsService _settings;
    private readonly MonitoringCoordinator _monitoring;
    private readonly INotifier _notifier;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly Dictionary<long, (RootState State, DateTimeOffset At)> _lastAlerts = new();
    private bool _initialized;

    public ShellViewModel(
        WatchedFoldersViewModel folders,
        TimelineViewModel timeline,
        SettingsViewModel settingsPage,
        DiagnosticsViewModel diagnostics,
        OnboardingViewModel onboarding,
        DialogHostViewModel dialogs,
        SettingsService settings,
        MonitoringCoordinator monitoring,
        DataResetService reset,
        INotifier notifier,
        IUiDispatcher ui,
        TimeProvider time)
    {
        Folders = folders;
        Timeline = timeline;
        SettingsPage = settingsPage;
        Diagnostics = diagnostics;
        Onboarding = onboarding;
        Dialogs = dialogs;
        _settings = settings;
        _monitoring = monitoring;
        _notifier = notifier;
        _ui = ui;
        _time = time;
        reset.DataDeleted += (_, _) => _ui.Post(OnDataDeleted);
        dialogs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DialogHostViewModel.IsOpen))
            {
                OnPropertyChanged(nameof(IsMainEnabled));
            }
        };
    }

    public event EventHandler? FocusSearchRequested;

    public WatchedFoldersViewModel Folders { get; }

    public TimelineViewModel Timeline { get; }

    public SettingsViewModel SettingsPage { get; }

    public DiagnosticsViewModel Diagnostics { get; }

    public OnboardingViewModel Onboarding { get; }

    public DialogHostViewModel Dialogs { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActivityPage), nameof(IsSettingsPage), nameof(IsDiagnosticsPage))]
    public partial ShellPage Page { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMainEnabled))]
    public partial bool IsOnboardingVisible { get; set; }

    public bool IsActivityPage => Page == ShellPage.Activity;

    public bool IsSettingsPage => Page == ShellPage.Settings;

    public bool IsDiagnosticsPage => Page == ShellPage.Diagnostics;

    public bool IsMainEnabled => !Dialogs.IsOpen && !IsOnboardingVisible;

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        Folders.Initialize();
        Timeline.Initialize();
        Folders.SelectionChanged += (_, rootId) =>
        {
            Timeline.SelectRoot(rootId, Folders.SelectedFolder?.Name);
            if (rootId is not null)
            {
                Page = ShellPage.Activity;
            }
        };
        Timeline.RootFilterChanged += (_, rootId) => Folders.SelectRoot(rootId);
        Folders.Folders.CollectionChanged += OnFoldersCollectionChanged;
        Timeline.HasWatchedFolders = Folders.HasFolders;
        Onboarding.Completed += (_, _) => IsOnboardingVisible = false;
        _settings.Changed += (_, updated) => _ui.Post(() =>
        {
            if (!updated.OnboardingCompleted)
            {
                IsOnboardingVisible = true;
                Page = ShellPage.Activity;
            }
        });
        _monitoring.AttentionRequired += (_, status) => _ui.Post(() => Alert(status));
        IsOnboardingVisible = !_settings.Current.OnboardingCompleted;
    }

    [RelayCommand]
    private void ShowActivity() => Page = ShellPage.Activity;

    [RelayCommand]
    private void ShowSettings() => Page = ShellPage.Settings;

    [RelayCommand]
    private async Task ShowDiagnostics()
    {
        Page = ShellPage.Diagnostics;
        await Diagnostics.RefreshAsync();
    }

    [RelayCommand]
    private async Task AddFolder()
    {
        if (IsMainEnabled)
        {
            await Folders.AddFolderInteractiveAsync();
        }
    }

    [RelayCommand]
    private void FocusSearch()
    {
        if (!IsMainEnabled)
        {
            return;
        }

        Page = ShellPage.Activity;
        FocusSearchRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ScanNow()
    {
        if (!IsMainEnabled)
        {
            return;
        }

        if (Folders.SelectedFolder is { } folder)
        {
            Folders.ScanNow(folder);
        }
        else
        {
            _monitoring.RequestScan();
        }

        _notifier.Notify("Scan requested", Folders.SelectedFolder is { } selected ? $"Scanning \"{selected.Name}\"." : "Scanning every available watched folder.");
    }

    [RelayCommand]
    private async Task ExportCsv()
    {
        if (IsMainEnabled)
        {
            Page = ShellPage.Activity;
            await Timeline.ExportCsvCommand.ExecuteAsync(null);
        }
    }

    /// <summary>Escape: close the dialog, then leave Settings/Diagnostics, then clear the selection.</summary>
    [RelayCommand]
    private void GoBack()
    {
        if (Dialogs.IsOpen)
        {
            Dialogs.CancelCurrentCommand.Execute(null);
        }
        else if (IsOnboardingVisible)
        {
            return;
        }
        else if (Page != ShellPage.Activity)
        {
            Page = ShellPage.Activity;
        }
        else
        {
            Timeline.SelectedRow = null;
        }
    }

    private void OnFoldersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Timeline.HasWatchedFolders = Folders.HasFolders;

    private void Alert(RootRuntimeStatus status)
    {
        if (!_settings.Current.NotifyOnMonitoringProblems)
        {
            return;
        }

        var now = _time.GetUtcNow();
        if (_lastAlerts.TryGetValue(status.RootId, out var last) && last.State == status.State && now - last.At < AlertCooldown)
        {
            return;
        }

        _lastAlerts[status.RootId] = (status.State, now);
        var title = status.State == RootState.Missing ? $"\"{status.Name}\" is missing" : $"\"{status.Name}\" needs attention";
        _notifier.Notify(title, status.Message ?? "Open FolderTrail to see what happened.", NoticeKind.Warning);
    }

    private void OnDataDeleted()
    {
        Folders.Reset();
        Timeline.SelectRoot(null);
        _ = Timeline.ReloadAsync();
        Page = ShellPage.Activity;
        IsOnboardingVisible = true;
    }
}
