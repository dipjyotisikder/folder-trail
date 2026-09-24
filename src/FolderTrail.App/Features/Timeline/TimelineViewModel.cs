using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderTrail.App.Shared;
using FolderTrail.App.Shared.Platform;
using FolderTrail.Core.Features.Monitoring;
using FolderTrail.Core.Features.Timeline;
using FolderTrail.Core.Features.WatchedFolders;
using FolderTrail.Core.SharedKernel.Diagnostics;
using FolderTrail.Core.SharedKernel.Journal;

namespace FolderTrail.App.Features.Timeline;

/// <summary>
/// The activity timeline: newest first, grouped by local day, paged with a keyset cursor so large histories stay
/// responsive. New live activity is prepended when the list is scrolled to the top, otherwise it's offered.
/// </summary>
public sealed partial class TimelineViewModel : ViewModelBase, IDisposable
{
    public const int PageSize = 200;
    private const int NewerBatchLimit = 500;

    private readonly TimelineService _timeline;
    private readonly MonitoringCoordinator _monitoring;
    private readonly WatchedFolderService _folders;
    private readonly IFileSavePicker _savePicker;
    private readonly INotifier _notifier;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly DiagnosticLog _log;

    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _searchDebounce;
    private TimelineFilter _activeFilter = TimelineFilter.All;
    private TimelineCursor? _nextCursor;
    private int _loadVersion;
    private int _previousSelectedIndex = -1;
    private bool _refreshScheduled;
    private bool _prepending;
    private bool _refreshAgain;
    private long _maxLoadedId;
    private readonly HashSet<long> _loadedIds = new();
    private bool _suppressReload;
    private bool _applyingExternalSelection;
    private ITimer? _clock;
    private DateOnly _today;

    public TimelineViewModel(
        TimelineService timeline,
        MonitoringCoordinator monitoring,
        WatchedFolderService folders,
        EventDetailsViewModel details,
        IFileSavePicker savePicker,
        INotifier notifier,
        IUiDispatcher ui,
        TimeProvider time,
        DiagnosticLog log)
    {
        _timeline = timeline;
        _monitoring = monitoring;
        _folders = folders;
        Details = details;
        _savePicker = savePicker;
        _notifier = notifier;
        _ui = ui;
        _time = time;
        _log = log;
        _today = Today();
        SelectedSource = SourceOptions[0];
        SelectedDateRange = DateRangeOptions[0];
        RootOptions.Add(AllFolders);
        SelectedRoot = AllFolders;
    }

    public event EventHandler<long?>? RootFilterChanged;

    public static RootFilterOption AllFolders { get; } = new(null, "All folders");

    public EventDetailsViewModel Details { get; }

    public ObservableCollection<TimelineRow> Rows { get; } = new();

    public ObservableCollection<RootFilterOption> RootOptions { get; } = new();

    public IReadOnlyList<Option<ActivitySource?>> SourceOptions { get; } =
    [
        new(null, "All sources"),
        new(ActivitySource.Live, "Live"),
        new(ActivitySource.Reconciliation, "Reconciliation"),
        new(ActivitySource.ManualScan, "Manual scan"),
    ];

    public IReadOnlyList<Option<DateRange>> DateRangeOptions { get; } =
    [
        new(DateRange.AnyTime, "Any time"),
        new(DateRange.Today, "Today"),
        new(DateRange.Last7Days, "Last 7 days"),
        new(DateRange.Last30Days, "Last 30 days"),
        new(DateRange.Custom, "Custom range"),
    ];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowCreated { get; set; }

    [ObservableProperty]
    public partial bool ShowModified { get; set; }

    [ObservableProperty]
    public partial bool ShowDeleted { get; set; }

    [ObservableProperty]
    public partial bool ShowRenamed { get; set; }

    [ObservableProperty]
    public partial Option<ActivitySource?>? SelectedSource { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomRange))]
    public partial Option<DateRange>? SelectedDateRange { get; set; }

    public bool IsCustomRange => SelectedDateRange?.Value == DateRange.Custom;

    [ObservableProperty]
    public partial DateTime? CustomFrom { get; set; }

    [ObservableProperty]
    public partial DateTime? CustomTo { get; set; }

    [ObservableProperty]
    public partial RootFilterOption? SelectedRoot { get; set; }

    [ObservableProperty]
    public partial TimelineRow? SelectedRow { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool IsLoadingMore { get; private set; }

    [ObservableProperty]
    public partial bool HasMore { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCsvCommand))]
    public partial bool IsExporting { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultSummary))]
    public partial long? TotalCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingNew), nameof(PendingNewText))]
    public partial int PendingNewCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyTitle), nameof(EmptyMessage))]
    public partial bool HasWatchedFolders { get; set; }

    /// <summary>Set by the view: live activity is only inserted directly while the user is at the top.</summary>
    public bool IsScrolledToTop { get; set; } = true;

    public bool IsEmpty => !IsLoading && Rows.Count == 0;

    /// <summary>Filters other than the selected folder, which is a view choice rather than a search.</summary>
    public bool FiltersActive => !(BuildFilter() with { RootIds = null }).IsEmpty;

    public bool IsFolderSelected => SelectedRoot?.RootId is not null;

    public bool ShowClearFilters => FiltersActive;

    public bool HasPendingNew => PendingNewCount > 0;

    public string PendingNewText => PendingNewCount >= NewerBatchLimit ? "Many new events · Show" : $"{Format.Count(PendingNewCount, "new event", "new events")} · Show";

    public string ResultSummary => TotalCount is not { } count
        ? string.Empty
        : FiltersActive ? $"{Format.Count(count, "event", "events")} match the filters" : Format.Count(count, "event", "events");

    public string EmptyTitle => FiltersActive
        ? "No matching activity"
        : IsFolderSelected
            ? "No activity recorded in this folder yet"
            : HasWatchedFolders ? "No activity recorded yet" : "No folders are being watched";

    public string EmptyMessage => FiltersActive
        ? "Try a different search, or clear the filters to see everything."
        : HasWatchedFolders || IsFolderSelected
            ? "Changes appear here as they happen. The first scan of a folder only creates a baseline, so it doesn't add records."
            : "Add a folder to start recording what changes in it and when. FolderTrail never stores file contents.";

    public void Initialize()
    {
        _monitoring.JournalAppended += OnJournalAppended;
        _folders.Changed += OnFoldersChanged;
        _clock = _time.CreateTimer(_ => _ui.Post(OnClockTick), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        _ = RefreshRootOptionsAsync();
        _ = ReloadAsync();
    }

    public void Dispose()
    {
        _monitoring.JournalAppended -= OnJournalAppended;
        _folders.Changed -= OnFoldersChanged;
        _clock?.Dispose();
        _loadCancellation?.Cancel();
    }

    /// <summary>Applies a folder chosen elsewhere (the sidebar) without echoing the change back.</summary>
    public void SelectRoot(long? rootId, string? label = null)
    {
        var option = RootOptions.FirstOrDefault(o => o.RootId == rootId);
        if (option is null && rootId is not null)
        {
            // The folder was just added and the options haven't been refreshed yet.
            option = new RootFilterOption(rootId, label ?? "Selected folder");
            RootOptions.Add(option);
        }

        option ??= AllFolders;
        if (Equals(option, SelectedRoot))
        {
            return;
        }

        _applyingExternalSelection = true;
        try
        {
            SelectedRoot = option;
        }
        finally
        {
            _applyingExternalSelection = false;
        }
    }

    public TimelineFilter BuildFilter()
    {
        var types = new List<ActivityType>();
        if (ShowCreated)
        {
            types.Add(ActivityType.Created);
        }

        if (ShowModified)
        {
            types.Add(ActivityType.Modified);
        }

        if (ShowDeleted)
        {
            types.Add(ActivityType.Deleted);
        }

        if (ShowRenamed)
        {
            types.Add(ActivityType.Renamed);
        }

        var (from, to) = DateBounds();
        return new TimelineFilter(
            string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            SelectedRoot?.RootId is { } rootId ? [rootId] : null,
            types.Count == 0 ? null : types,
            SelectedSource?.Value is { } source ? [source] : null,
            from,
            to);
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        _loadCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        var version = ++_loadVersion;
        IsLoading = true;
        PendingNewCount = 0;
        try
        {
            var filter = BuildFilter();
            var pageTask = _timeline.GetPageAsync(filter, null, PageSize, cancellation.Token);
            var countTask = _timeline.CountAsync(filter, cancellation.Token);
            var page = await pageTask;
            if (version != _loadVersion)
            {
                return;
            }

            _activeFilter = filter;
            ReplaceRows(page.Items);
            _nextCursor = page.Next;
            HasMore = page.Next is not null;
            var count = await countTask;
            if (version == _loadVersion)
            {
                TotalCount = count;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _log.Error("UI", "Loading activity failed.", exception);
            _notifier.Notify("Couldn't load activity", exception.Message, NoticeKind.Error);
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
                RaiseStateChanged();
            }
        }
    }

    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        if (!HasMore || IsLoadingMore || IsLoading || _nextCursor is null)
        {
            return;
        }

        IsLoadingMore = true;
        var version = _loadVersion;
        try
        {
            var page = await _timeline.GetPageAsync(_activeFilter, _nextCursor, PageSize);
            if (version != _loadVersion)
            {
                return;
            }

            AppendRows(page.Items);
            _nextCursor = page.Next;
            HasMore = page.Next is not null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Error("UI", "Loading more activity failed.", exception);
            _notifier.Notify("Couldn't load more activity", exception.Message, NoticeKind.Error);
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    [RelayCommand]
    private Task ShowNew() => ReloadAsync();

    [RelayCommand]
    private void ClearFilters()
    {
        _suppressReload = true;
        try
        {
            SearchText = string.Empty;
            ShowCreated = ShowModified = ShowDeleted = ShowRenamed = false;
            SelectedSource = SourceOptions[0];
            SelectedDateRange = DateRangeOptions[0];
            CustomFrom = CustomTo = null;
            SelectedRoot = AllFolders;
        }
        finally
        {
            _suppressReload = false;
        }

        _ = ReloadAsync();
    }

    private bool CanExport() => !IsExporting;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportCsv()
    {
        var filter = _activeFilter;
        var suggested = string.Create(CultureInfo.InvariantCulture, $"FolderTrail activity {Format.ToLocal(_time.GetUtcNow(), _time.LocalTimeZone):yyyy-MM-dd HHmm}.csv");
        IsExporting = true;
        try
        {
            var target = await _savePicker.PickSaveFileAsync(new SaveFileRequest("Export activity to CSV", suggested, "csv", "CSV file"));
            if (target is not { } file)
            {
                return;
            }

            int count;
            await using (file.Stream)
            {
                count = await _timeline.ExportCsvAsync(filter, file.Stream, _time.LocalTimeZone);
            }

            _notifier.Notify("Export complete", $"Saved {Format.Count(count, "event", "events")} to {file.FileName}.", NoticeKind.Success);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Error("UI", "Exporting activity failed.", exception);
            _notifier.Notify("Export failed", exception.Message, NoticeKind.Error);
        }
        finally
        {
            IsExporting = false;
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_suppressReload)
        {
            return;
        }

        _searchDebounce?.Cancel();
        var debounce = new CancellationTokenSource();
        _searchDebounce = debounce;
        _ = Task.Delay(TimeSpan.FromMilliseconds(300), _time, debounce.Token).ContinueWith(
            t =>
            {
                if (!t.IsCanceled)
                {
                    _ui.Post(() => _ = ReloadAsync());
                }
            },
            TaskScheduler.Default);
    }

    partial void OnShowCreatedChanged(bool value) => ReloadForFilter();

    partial void OnShowModifiedChanged(bool value) => ReloadForFilter();

    partial void OnShowDeletedChanged(bool value) => ReloadForFilter();

    partial void OnShowRenamedChanged(bool value) => ReloadForFilter();

    partial void OnSelectedSourceChanged(Option<ActivitySource?>? value)
    {
        if (value is null)
        {
            SelectedSource = SourceOptions[0];
            return;
        }

        ReloadForFilter();
    }

    partial void OnSelectedDateRangeChanged(Option<DateRange>? value)
    {
        if (value is null)
        {
            SelectedDateRange = DateRangeOptions[0];
            return;
        }

        if (value.Value == DateRange.Custom && CustomFrom is null && CustomTo is null)
        {
            CustomTo = Format.ToLocal(_time.GetUtcNow(), _time.LocalTimeZone).Date;
            CustomFrom = CustomTo.Value.AddDays(-7);
        }

        ReloadForFilter();
    }

    partial void OnCustomFromChanged(DateTime? value) => ReloadForFilter(onlyIfCustom: true);

    partial void OnCustomToChanged(DateTime? value) => ReloadForFilter(onlyIfCustom: true);

    partial void OnSelectedRootChanged(RootFilterOption? value)
    {
        if (value is null)
        {
            if (!_suppressReload)
            {
                SelectedRoot = AllFolders;
            }

            return;
        }

        if (!_applyingExternalSelection && !_suppressReload)
        {
            RootFilterChanged?.Invoke(this, value.RootId);
        }

        ReloadForFilter();
    }

    partial void OnSelectedRowChanged(TimelineRow? value)
    {
        var index = value is null ? -1 : Rows.IndexOf(value);
        if (value is DayHeaderRow && index >= 0)
        {
            // Headers aren't selectable: keep moving in the direction the user was going.
            var next = index >= _previousSelectedIndex ? index + 1 : index - 1;
            if (next < 0 || next >= Rows.Count)
            {
                next = index + 1 < Rows.Count ? index + 1 : index - 1;
            }

            SelectedRow = next >= 0 && next < Rows.Count ? Rows[next] : null;
            return;
        }

        _previousSelectedIndex = index;
        _ = Details.ShowAsync((value as EventRow)?.Entry);
        if (index >= 0 && index > Rows.Count - 25)
        {
            _ = LoadMoreAsync();
        }
    }

    private void ReloadForFilter(bool onlyIfCustom = false)
    {
        if (_suppressReload || (onlyIfCustom && !IsCustomRange))
        {
            return;
        }

        _ = ReloadAsync();
    }

    private (DateTimeOffset? From, DateTimeOffset? To) DateBounds()
    {
        var zone = _time.LocalTimeZone;
        var today = Format.ToLocal(_time.GetUtcNow(), zone).Date;
        DateTimeOffset StartOf(DateTime localDate) => new(localDate, zone.GetUtcOffset(localDate));
        return (SelectedDateRange?.Value ?? DateRange.AnyTime) switch
        {
            DateRange.Today => (StartOf(today), null),
            DateRange.Last7Days => (StartOf(today.AddDays(-6)), null),
            DateRange.Last30Days => (StartOf(today.AddDays(-29)), null),
            DateRange.Custom => (
                CustomFrom is { } from ? StartOf(from.Date) : null,
                CustomTo is { } to ? StartOf(to.Date.AddDays(1)) : null),
            _ => (null, null),
        };
    }

    private void ReplaceRows(IReadOnlyList<TimelineEntry> entries)
    {
        SelectedRow = null;
        _previousSelectedIndex = -1;
        Rows.Clear();
        _loadedIds.Clear();
        _maxLoadedId = 0;
        AppendRows(entries);
        _ = Details.ShowAsync(null);
    }

    private void AppendRows(IReadOnlyList<TimelineEntry> entries)
    {
        var lastDay = Rows.OfType<EventRow>().LastOrDefault()?.Day;
        foreach (var entry in entries)
        {
            if (!Track(entry))
            {
                continue;
            }

            var row = new EventRow(entry, _time.LocalTimeZone);
            if (row.Day != lastDay)
            {
                Rows.Add(new DayHeaderRow(row.Day, Format.DayHeader(row.Day, _today)));
                lastDay = row.Day;
            }

            Rows.Add(row);
        }

        RaiseStateChanged();
    }

    private void PrependRows(IReadOnlyList<TimelineEntry> newestFirst)
    {
        var existingHeader = Rows.FirstOrDefault() as DayHeaderRow;
        var groups = newestFirst.Where(Track).Select(e => new EventRow(e, _time.LocalTimeZone)).GroupBy(r => r.Day).ToList();
        var index = 0;
        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            if (g == groups.Count - 1 && existingHeader?.Day == group.Key)
            {
                index++;
            }
            else
            {
                Rows.Insert(index++, new DayHeaderRow(group.Key, Format.DayHeader(group.Key, _today)));
            }

            foreach (var row in group)
            {
                Rows.Insert(index++, row);
            }
        }

        RaiseStateChanged();
    }

    private void OnJournalAppended(object? sender, JournalAppendedEventArgs e)
    {
        _ui.Post(() =>
        {
            if (_refreshScheduled)
            {
                return;
            }

            _refreshScheduled = true;
            _ = Task.Delay(TimeSpan.FromMilliseconds(750), _time).ContinueWith(_ => _ui.Post(() => _ = PrependNewerAsync()), TaskScheduler.Default);
        });
    }

    private async Task PrependNewerAsync()
    {
        _refreshScheduled = false;
        if (_prepending)
        {
            // One refresh at a time; the running one repeats once when it finishes.
            _refreshAgain = true;
            return;
        }

        if (IsLoading)
        {
            return;
        }

        var newest = Rows.OfType<EventRow>().FirstOrDefault();
        if (newest is null)
        {
            await ReloadAsync();
            return;
        }

        _prepending = true;
        try
        {
            var version = _loadVersion;
            var added = (await _timeline.GetAddedAfterAsync(_activeFilter, _maxLoadedId, NewerBatchLimit))
                .Where(e => !_loadedIds.Contains(e.Id))
                .ToList();
            if (version != _loadVersion || added.Count == 0)
            {
                return;
            }

            _maxLoadedId = Math.Max(_maxLoadedId, added.Max(e => e.Id));
            if (HasMore && Rows.OfType<EventRow>().LastOrDefault()?.Entry.Cursor is { } oldest)
            {
                // Entries older than everything loaded arrive with a later page.
                added = added.Where(e => e.DetectedUtc > oldest.DetectedUtc || (e.DetectedUtc == oldest.DetectedUtc && e.Id > oldest.Id)).ToList();
                if (added.Count == 0)
                {
                    return;
                }
            }

            // Entries dated earlier than the top row (held or merged live events) belong further down the list.
            var fitsAtTop = added.All(e => e.DetectedUtc > newest.Entry.DetectedUtc
                                           || (e.DetectedUtc == newest.Entry.DetectedUtc && e.Id > newest.Entry.Id));
            if (!IsScrolledToTop || added.Count >= NewerBatchLimit)
            {
                PendingNewCount += added.Count;
                return;
            }

            if (!fitsAtTop)
            {
                await ReloadAsync();
                return;
            }

            PrependRows(added);
            TotalCount = (TotalCount ?? 0) + added.Count;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Warning("UI", "Refreshing live activity failed.", exception);
        }
        finally
        {
            _prepending = false;
            if (_refreshAgain)
            {
                _refreshAgain = false;
                _ui.Post(() => _ = PrependNewerAsync());
            }
        }
    }

    /// <returns>False when the entry is already shown.</returns>
    private bool Track(TimelineEntry entry)
    {
        if (!_loadedIds.Add(entry.Id))
        {
            return false;
        }

        _maxLoadedId = Math.Max(_maxLoadedId, entry.Id);
        return true;
    }

    private void OnFoldersChanged(object? sender, WatchedFolderChange change) => _ui.Post(() => _ = RefreshRootOptionsAsync(change));

    private async Task RefreshRootOptionsAsync(WatchedFolderChange? change = null)
    {
        try
        {
            var options = await _timeline.GetRootOptionsAsync();
            var selectedId = SelectedRoot?.RootId;
            _suppressReload = true;
            try
            {
                RootOptions.Clear();
                RootOptions.Add(AllFolders);
                foreach (var option in options)
                {
                    RootOptions.Add(new RootFilterOption(option.RootId, option.IsRemoved ? $"{option.Name} (removed)" : option.Name));
                }

                SelectedRoot = RootOptions.FirstOrDefault(o => o.RootId == selectedId) ?? AllFolders;
            }
            finally
            {
                _suppressReload = false;
            }

            if (change is { Kind: WatchedFolderChangeKind.Removed, Removal: RemovalMode.DeleteHistory } || selectedId != SelectedRoot?.RootId)
            {
                await ReloadAsync();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Warning("UI", "Refreshing the folder filter failed.", exception);
        }
    }

    private void OnClockTick()
    {
        var today = Today();
        if (today == _today)
        {
            return;
        }

        _today = today;
        foreach (var header in Rows.OfType<DayHeaderRow>())
        {
            header.Title = Format.DayHeader(header.Day, today);
        }
    }

    private DateOnly Today() => DateOnly.FromDateTime(Format.ToLocal(_time.GetUtcNow(), _time.LocalTimeZone).DateTime);

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(FiltersActive));
        OnPropertyChanged(nameof(ShowClearFilters));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(ResultSummary));
    }
}
