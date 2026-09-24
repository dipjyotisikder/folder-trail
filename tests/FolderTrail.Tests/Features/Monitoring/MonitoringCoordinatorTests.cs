using FolderTrail.Core.Features.Monitoring;
using FolderTrail.Core.Features.Timeline;
using FolderTrail.Core.Features.WatchedFolders;
using FolderTrail.Core.SharedKernel.Journal;
using FolderTrail.Core.SharedKernel.Paths;
using FolderTrail.Tests.Support;

namespace FolderTrail.Tests.Features.Monitoring;

public sealed class MonitoringCoordinatorTests : IDisposable
{
    private readonly TempDirectory _folder = new();
    private readonly FakeWatcherFactory _watchers = new();
    private readonly CoreHarness _harness;

    public MonitoringCoordinatorTests() => _harness = new CoreHarness(watchers: _watchers);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _harness.Dispose();
        _folder.Dispose();
    }

    private Task WaitForStateAsync(long rootId, RootState state) =>
        Eventually.TrueAsync(() => _harness.Monitoring.GetStatus(rootId)?.State == state, $"folder #{rootId} is {state} (now {_harness.Monitoring.GetStatus(rootId)?.State})");

    private async Task<IReadOnlyList<MonitoringGap>> GapsAsync(long rootId) => await _harness.Gaps.GetRecentAsync(rootId, 50, Token);

    /// <summary>Moves fake time forward in steps so the coordinator's timer loop observes each tick.</summary>
    private async Task AdvanceAsync(TimeSpan total, TimeSpan step)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += step)
        {
            _harness.Time.Advance(step);
            await Task.Delay(30, Token);
        }
    }

    [WindowsFact]
    public async Task A_new_folder_gets_a_baseline_scan_then_is_watched_without_activity()
    {
        _folder.CreateFile("a.txt");
        _folder.CreateFile(@"sub\b.txt");
        var root = await _harness.AddRootAsync(_folder.Path);
        var states = new List<RootState>();
        _harness.Monitoring.StatusChanged += (_, s) => { lock (states) { states.Add(s.State); } };

        await _harness.Monitoring.StartAsync(Token);
        await WaitForStateAsync(root.Id, RootState.Watching);

        var status = _harness.Monitoring.GetStatus(root.Id)!;
        Assert.True(status.IsLiveWatcherActive);
        Assert.Equal(3, status.BaselineItemCount);
        Assert.NotNull(status.LastSuccessfulScanUtc);
        lock (states)
        {
            Assert.Contains(RootState.BaselineScan, states);
        }

        Assert.Empty(await _harness.EventsAsync());
        Assert.NotNull(_watchers.ActiveFor(root.Id));
    }

    [WindowsFact]
    public async Task Folders_added_while_running_are_attached_immediately()
    {
        await _harness.Monitoring.StartAsync(Token);
        await _harness.Monitoring.Initialized;

        var root = await _harness.AddRootAsync(_folder.Path);

        await WaitForStateAsync(root.Id, RootState.Watching);
    }

    [WindowsFact]
    public async Task Live_notifications_from_the_watcher_are_recorded()
    {
        var root = await _harness.AddRootAsync(_folder.Path);
        await _harness.Monitoring.StartAsync(Token);
        await WaitForStateAsync(root.Id, RootState.Watching);

        var file = _folder.CreateFile("live.txt");
        _watchers.ActiveFor(root.Id)!.Emit(RawEventKind.Created, file, at: _harness.Time.GetUtcNow());

        await Eventually.TrueAsync(async () => (await _harness.EventsAsync()).Count == 1, "the live event is written");
        var entry = (await _harness.EventsAsync())[0];
        Assert.Equal(ActivitySource.Live, entry.Source);
        await Eventually.TrueAsync(() => _harness.Monitoring.GetStatus(root.Id)?.LastEventUtc is not null, "the last event time is shown");
    }

    [WindowsFact]
    public async Task A_buffer_overflow_opens_a_gap_and_the_catch_up_scan_closes_it()
    {
        var root = await _harness.AddRootAsync(_folder.Path);
        await _harness.Monitoring.StartAsync(Token);
        await WaitForStateAsync(root.Id, RootState.Watching);

        _folder.CreateFile("missed-by-watcher.txt");
        _watchers.ActiveFor(root.Id)!.Fail(overflow: true);

        await Eventually.TrueAsync(async () => (await _harness.EventsAsync()).Count == 1, "the scan finds the missed file");
        var entry = (await _harness.EventsAsync())[0];
        Assert.Equal(ActivitySource.Reconciliation, entry.Source);
        await Eventually.TrueAsync(async () => (await GapsAsync(root.Id)).Any(g => g.Reason == GapReason.EventOverflow && !g.IsOpen), "the overflow gap is closed");
        await WaitForStateAsync(root.Id, RootState.Watching);
        Assert.Equal(0, _harness.Monitoring.GetStatus(root.Id)!.OpenGapCount);
    }

    [WindowsFact]
    public async Task A_watcher_error_restarts_the_watcher_and_rescans()
    {
        var root = await _harness.AddRootAsync(_folder.Path);
        await _harness.Monitoring.StartAsync(Token);
        await WaitForStateAsync(root.Id, RootState.Watching);
        var first = _watchers.ActiveFor(root.Id)!;

        first.Fail(overflow: false);

        await Eventually.TrueAsync(() => first.Disposed && _watchers.ActiveFor(root.Id) is not null, "a new watcher replaces the failed one");
        await Eventually.TrueAsync(async () => (await GapsAsync(root.Id)).Any(g => g.Reason == GapReason.WatcherError && !g.IsOpen), "the error gap is closed by a scan");
    }

    [WindowsFact]
    public async Task A_folder_that_disappears_is_missing_and_recovers_when_it_returns()
    {
        var watched = _folder.CreateDirectory("watched");
        _folder.CreateFile(@"watched\a.txt");
        var root = await _harness.AddRootAsync(watched);
        var attention = new List<RootRuntimeStatus>();
        _harness.Monitoring.AttentionRequired += (_, s) => { lock (attention) { attention.Add(s); } };
        await _harness.Monitoring.StartAsync(Token);
        await WaitForStateAsync(root.Id, RootState.Watching);

        Directory.Delete(watched, recursive: true);
        await AdvanceAsync(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1));
        await WaitForStateAsync(root.Id, RootState.Missing);
        var missing = _harness.Monitoring.GetStatus(root.Id)!;
        Assert.False(missing.IsLiveWatcherActive);
        Assert.Contains("moved, renamed, or deleted", missing.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(watched));
        lock (attention)
        {
            Assert.Contains(attention, s => s.State == RootState.Missing);
        }

        Directory.CreateDirectory(watched);
        await AdvanceAsync(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1));
        await WaitForStateAsync(root.Id, RootState.Watching);
        Assert.Contains(await _harness.EventsAsync(), e => e.Type == ActivityType.Deleted && e.Name == "a.txt");
        Assert.All(await GapsAsync(root.Id), g => Assert.False(g.IsOpen));
    }

    [WindowsFact]
    public async Task Pausing_stops_watching_and_resuming_records_net_changes_and_closes_the_gap()
    {
        var root = await _harness.AddRootAsync(_folder.Path);
        await _harness.Monitoring.StartAsync(Token);
        await WaitForStateAsync(root.Id, RootState.Watching);
        var watcher = _watchers.ActiveFor(root.Id)!;

        await _harness.Folders.PauseAsync(root.Id, Token);
        await WaitForStateAsync(root.Id, RootState.Paused);
        Assert.True(watcher.Disposed);
        Assert.Contains(await GapsAsync(root.Id), g => g.Reason == GapReason.Paused && g.IsOpen);

        _folder.CreateFile("while-paused.txt");
        await _harness.Folders.ResumeAsync(root.Id, Token);
        await Eventually.TrueAsync(async () => (await _harness.EventsAsync()).Count == 1, "the resume scan records the change");
        await WaitForStateAsync(root.Id, RootState.Watching);
        Assert.All(await GapsAsync(root.Id), g => Assert.False(g.IsOpen));
    }

    [WindowsFact]
    public async Task Restarting_the_app_records_a_not_running_gap_and_catches_up()
    {
        var root = await _harness.AddRootAsync(_folder.Path);
        await _harness.Monitoring.StartAsync(Token);
        await WaitForStateAsync(root.Id, RootState.Watching);
        await _harness.Monitoring.StopAsync(Token);

        _folder.CreateFile("while-closed.txt");
        _harness.Time.Advance(TimeSpan.FromHours(5));
        await _harness.Monitoring.StartAsync(Token);

        await Eventually.TrueAsync(async () => (await _harness.EventsAsync()).Count == 1, "the startup scan records the change");
        await WaitForStateAsync(root.Id, RootState.Watching);
        var gap = Assert.Single(await GapsAsync(root.Id), g => g.Reason == GapReason.AppNotRunning);
        Assert.False(gap.IsOpen);
        Assert.True(gap.EndUtc - gap.StartUtc >= TimeSpan.FromHours(5));
    }

    [WindowsFact]
    public async Task A_watcher_that_cannot_start_needs_attention_but_scans_still_run()
    {
        _watchers.FailToStart = true;
        var root = await _harness.AddRootAsync(_folder.Path);

        await _harness.Monitoring.StartAsync(Token);

        await WaitForStateAsync(root.Id, RootState.NeedsAttention);
        var status = _harness.Monitoring.GetStatus(root.Id)!;
        Assert.False(status.IsLiveWatcherActive);
        Assert.NotNull(status.BaselineCompletedUtc);
        Assert.Contains("Scans still detect changes", status.Message, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task Waking_from_sleep_is_detected_as_a_gap_and_triggers_a_scan()
    {
        var root = await _harness.AddRootAsync(_folder.Path);
        await _harness.Monitoring.StartAsync(Token);
        await WaitForStateAsync(root.Id, RootState.Watching);

        _folder.CreateFile("while-asleep.txt");
        _harness.Time.Advance(TimeSpan.FromMinutes(45));

        await Eventually.TrueAsync(async () => (await GapsAsync(root.Id)).Any(g => g.Reason == GapReason.SystemSuspended), "a suspension gap is recorded");
        await Eventually.TrueAsync(async () => (await _harness.EventsAsync()).Count == 1, "the resume scan records the change");
    }

    [WindowsFact]
    public async Task Manual_scan_requests_are_labelled_manual()
    {
        var root = await _harness.AddRootAsync(_folder.Path);
        await _harness.Monitoring.StartAsync(Token);
        await WaitForStateAsync(root.Id, RootState.Watching);

        _folder.CreateFile("found-by-hand.txt");
        _harness.Monitoring.RequestScan(root.Id);

        await Eventually.TrueAsync(async () => (await _harness.EventsAsync()).Count == 1, "the manual scan records the change");
        Assert.Equal(ActivitySource.ManualScan, (await _harness.EventsAsync())[0].Source);
    }

    [WindowsFact]
    public async Task A_folder_without_a_live_watcher_still_gets_periodic_scans()
    {
        _watchers.FailToStart = true;
        await _harness.Settings.UpdateAsync(s => s with { PeriodicScanMinutes = 15 }, Token);
        var root = await _harness.AddRootAsync(_folder.Path);
        await _harness.Monitoring.StartAsync(Token);
        await WaitForStateAsync(root.Id, RootState.NeedsAttention);
        await Eventually.TrueAsync(() => _harness.Monitoring.GetStatus(root.Id)?.BaselineCompletedUtc is not null, "the baseline completes");

        _folder.CreateFile("found-by-periodic-scan.txt");
        await AdvanceAsync(TimeSpan.FromMinutes(16), TimeSpan.FromSeconds(30));

        await Eventually.TrueAsync(async () => (await _harness.EventsAsync()).Count == 1, "a periodic scan records the change");
        Assert.Equal(ActivitySource.Reconciliation, (await _harness.EventsAsync())[0].Source);
    }

    [WindowsFact]
    public async Task A_scan_that_fails_unexpectedly_is_retried_instead_of_staying_stuck()
    {
        var scanner = new ThrowOnceScanner(new FolderTrail.Core.Features.Reconciliation.FileSystemScanner(_harness.Probe, _harness.Time));
        using var harness = new CoreHarness(scanner: scanner, watchers: new FakeWatcherFactory());
        var root = await harness.AddRootAsync(_folder.Path);
        await harness.Monitoring.StartAsync(Token);

        await Eventually.TrueAsync(() => harness.Monitoring.GetStatus(root.Id)?.State == RootState.NeedsAttention, "the failed scan is reported");
        for (var i = 0; i < 20 && harness.Monitoring.GetStatus(root.Id)?.State != RootState.Watching; i++)
        {
            harness.Time.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(50, Token);
        }

        await Eventually.TrueAsync(() => harness.Monitoring.GetStatus(root.Id)?.State == RootState.Watching, "the retry succeeds");
        Assert.NotNull(harness.Monitoring.GetStatus(root.Id)!.BaselineCompletedUtc);
    }

    private sealed class ThrowOnceScanner(FolderTrail.Core.Features.Reconciliation.IFileSystemScanner inner) : FolderTrail.Core.Features.Reconciliation.IFileSystemScanner
    {
        private int _calls;

        public FolderTrail.Core.Features.Reconciliation.ScanOutcome Scan(FolderTrail.Core.Features.Reconciliation.ScanRequest request, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _calls) == 1
                ? throw new NotSupportedException("Simulated unexpected failure.")
                : inner.Scan(request, cancellationToken);
    }

    [WindowsFact]
    public async Task Changes_found_by_a_scan_are_announced_like_live_changes()
    {
        var root = await _harness.AddRootAsync(_folder.Path);
        await _harness.Monitoring.StartAsync(Token);
        await WaitForStateAsync(root.Id, RootState.Watching);
        var announced = new List<JournalAppendedEventArgs>();
        _harness.Monitoring.JournalAppended += (_, e) => { lock (announced) { announced.Add(e); } };

        _folder.CreateFile("one.txt");
        _folder.CreateFile("two.txt");
        _harness.Monitoring.RequestScan(root.Id);

        await Eventually.TrueAsync(() => { lock (announced) { return announced.Count == 1; } }, "the scan's changes are announced");
        lock (announced)
        {
            Assert.Equal(2, announced[0].EventCount);
            Assert.Equal([root.Id], announced[0].RootIds);
        }
    }

    [WindowsFact]
    public async Task Removing_a_folder_stops_its_watcher_and_forgets_its_status()
    {
        var root = await _harness.AddRootAsync(_folder.Path);
        await _harness.Monitoring.StartAsync(Token);
        await WaitForStateAsync(root.Id, RootState.Watching);
        var watcher = _watchers.ActiveFor(root.Id)!;

        await _harness.Folders.RemoveAsync(root.Id, RemovalMode.KeepHistory, Token);

        await Eventually.TrueAsync(() => _harness.Monitoring.GetStatus(root.Id) is null, "the status is removed");
        Assert.True(watcher.Disposed);
        Assert.Empty(await _harness.InventoryAsync(root.Id));
    }

    [WindowsFact]
    public async Task Stopping_writes_pending_live_changes_before_returning()
    {
        var root = await _harness.AddRootAsync(_folder.Path);
        await _harness.Monitoring.StartAsync(Token);
        await WaitForStateAsync(root.Id, RootState.Watching);

        var file = _folder.CreateFile("pending.txt");
        _watchers.ActiveFor(root.Id)!.Emit(RawEventKind.Changed, file, at: _harness.Time.GetUtcNow());
        await _harness.Monitoring.StopAsync(Token);

        Assert.Equal(ActivityType.Modified, Assert.Single(await _harness.EventsAsync()).Type);
        Assert.False(_harness.Monitoring.IsRunning);
    }
}

public sealed class RealFileSystemWatcherTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [WindowsFact]
    public async Task The_real_watcher_records_create_modify_rename_and_delete_and_keeps_the_inventory_in_sync()
    {
        using var folder = new TempDirectory();
        using var harness = new CoreHarness(realWatchers: true);
        var root = await harness.AddRootAsync(folder.Path);
        await harness.Monitoring.StartAsync(Token);
        await Eventually.TrueAsync(() => harness.Monitoring.GetStatus(root.Id)?.State == RootState.Watching, "the folder is watched");

        async Task<TimelineEntry> WaitFor(ActivityType type, string name, Func<TimelineEntry, bool>? matches = null)
        {
            TimelineEntry? found = null;
            await Eventually.TrueAsync(
                async () =>
                {
                    harness.Time.Advance(TimeSpan.FromSeconds(2));
                    found = (await harness.EventsAsync()).FirstOrDefault(e => e.Type == type && e.Name == name && (matches?.Invoke(e) ?? true));
                    return found is not null;
                },
                $"{type} {name} is recorded");
            return found!;
        }

        var draft = folder.CreateFile("draft.txt", "v1");
        var created = await WaitFor(ActivityType.Created, "draft.txt");
        Assert.Equal(ActivitySource.Live, created.Source);

        File.AppendAllText(draft, " and more");
        var modified = await WaitFor(ActivityType.Modified, "draft.txt", e => e.Length == 11);
        Assert.Equal(ActivitySource.Live, modified.Source);

        var final = folder.Combine("final.txt");
        File.Move(draft, final);
        var renamed = await WaitFor(ActivityType.Renamed, "final.txt");
        Assert.Equal(WindowsPath.Canonicalize(draft), renamed.OldPath);

        folder.CreateFile(@"reports\q3.csv", "1,2,3");
        await WaitFor(ActivityType.Created, "q3.csv");

        File.Delete(final);
        var deleted = await WaitFor(ActivityType.Deleted, "final.txt");
        Assert.Equal(11, deleted.PreviousLength);

        await harness.Monitoring.StopAsync(Token);
        var check = await harness.Reconciliation.RunAsync(root.Id, FolderTrail.Core.Features.Reconciliation.ReconciliationTrigger.Manual, cancellationToken: Token);
        Assert.Equal(0, check.TotalChanges);
    }
}
