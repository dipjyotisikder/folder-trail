using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using FolderTrail.App.Shared;
using FolderTrail.Core.Features.Monitoring;

namespace FolderTrail.App.Features.WatchedFolders;

public interface IWatchedFolderActions
{
    void ScanNow(WatchedFolderItemViewModel folder);

    Task PauseAsync(WatchedFolderItemViewModel folder);

    Task ResumeAsync(WatchedFolderItemViewModel folder);

    void Retry(WatchedFolderItemViewModel folder);

    Task EditExclusionsAsync(WatchedFolderItemViewModel folder);

    Task RemoveAsync(WatchedFolderItemViewModel folder);

    void OpenInExplorer(WatchedFolderItemViewModel folder);
}

public sealed partial class WatchedFolderItemViewModel : ViewModelBase
{
    private readonly IWatchedFolderActions _actions;
    private readonly TimeProvider _time;
    private RootRuntimeStatus _status;

    public WatchedFolderItemViewModel(RootRuntimeStatus status, IWatchedFolderActions actions, TimeProvider time)
    {
        _status = status;
        _actions = actions;
        _time = time;
    }

    public long RootId => _status.RootId;

    public string Name => _status.Name;

    public string DisplayPath => _status.DisplayPath;

    public RootState State => _status.State;

    public string StateText => RootRuntimeStatus.Describe(State);

    public string? Message => _status.Message;

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public bool IsToneSuccess => State == RootState.Watching;

    public bool IsToneInfo => State is RootState.Scanning or RootState.BaselineScan;

    public bool IsToneWarning => State == RootState.NeedsAttention;

    public bool IsToneDanger => State == RootState.Missing;

    public bool IsPaused => State == RootState.Paused;

    public bool CanPause => !IsPaused;

    public bool CanResume => IsPaused;

    public bool NeedsRetry => State is RootState.Missing or RootState.NeedsAttention;

    public bool CanScan => State is RootState.Watching or RootState.Scanning;

    public string SidebarStatusText => State switch
    {
        RootState.Watching when _status.LastScanSkippedCount > 0 => $"Watching · {Format.Count(_status.LastScanSkippedCount, "item", "items")} skipped",
        RootState.Watching => _status.LastEventUtc is null ? "Watching · no activity yet" : "Watching · last event " + Relative(_status.LastEventUtc).ToLowerInvariant(),
        RootState.BaselineScan => "Creating baseline…",
        RootState.Scanning => "Scanning…",
        RootState.Paused => "Paused",
        RootState.Missing => "Missing",
        _ => "Needs attention",
    };

    public string LastScanText => Relative(_status.LastSuccessfulScanUtc);

    public string LastEventText => _status.LastEventUtc is null ? "None yet" : Relative(_status.LastEventUtc);

    public string BaselineText => _status.BaselineCompletedUtc is null
        ? "In progress"
        : Format.Count(_status.BaselineItemCount ?? 0, "item", "items") + " indexed";

    public string SkippedText => _status.LastScanSkippedCount == 0 ? "None" : Format.Count(_status.LastScanSkippedCount, "item", "items");

    public bool HasSkipped => _status.LastScanSkippedCount > 0;

    public int OpenGapCount => _status.OpenGapCount;

    public string GapsText => _status.OpenGapCount == 0 ? "None open" : Format.Count(_status.OpenGapCount, "open gap", "open gaps");

    public bool HasOpenGaps => _status.OpenGapCount > 0;

    public string WatcherText => _status.IsLiveWatcherActive ? "Running" : "Not running";

    public string ScopeText => _status.IncludeSubfolders ? "Folder and subfolders" : "This folder only";

    public string AutomationName => $"{Name}, {StateText}";

    public ObservableCollection<GapItemViewModel> RecentGaps { get; } = new();

    public bool HasRecentGaps => RecentGaps.Count > 0;

    public RootRuntimeStatus Status => _status;

    public void Apply(RootRuntimeStatus status)
    {
        _status = status;
        OnPropertyChanged(string.Empty);
    }

    public void SetGaps(IEnumerable<GapItemViewModel> gaps)
    {
        RecentGaps.Clear();
        foreach (var gap in gaps)
        {
            RecentGaps.Add(gap);
        }

        OnPropertyChanged(nameof(HasRecentGaps));
    }

    public void RefreshRelativeTimes()
    {
        OnPropertyChanged(nameof(SidebarStatusText));
        OnPropertyChanged(nameof(LastScanText));
        OnPropertyChanged(nameof(LastEventText));
    }

    [RelayCommand]
    private void ScanNow() => _actions.ScanNow(this);

    [RelayCommand]
    private Task Pause() => _actions.PauseAsync(this);

    [RelayCommand]
    private Task Resume() => _actions.ResumeAsync(this);

    [RelayCommand]
    private void Retry() => _actions.Retry(this);

    [RelayCommand]
    private Task EditExclusions() => _actions.EditExclusionsAsync(this);

    [RelayCommand]
    private Task Remove() => _actions.RemoveAsync(this);

    [RelayCommand]
    private void OpenInExplorer() => _actions.OpenInExplorer(this);

    private string Relative(DateTimeOffset? value) => Format.Relative(value, _time.GetUtcNow(), _time.LocalTimeZone);
}

public sealed class GapItemViewModel(MonitoringGap gap, TimeZoneInfo zone)
{
    public string Reason => MonitoringGap.Describe(gap.Reason);

    public bool IsOpen => gap.IsOpen;

    public string StatusText => gap.IsOpen ? "Open" : "Closed";

    public string Period
    {
        get
        {
            var from = gap.StartUtc is { } start ? Format.LocalDateTime(start, zone) : "an unknown time";
            return gap.EndUtc is { } end ? $"{from} to {Format.LocalDateTime(end, zone)}" : $"Since {from} (ongoing)";
        }
    }
}
