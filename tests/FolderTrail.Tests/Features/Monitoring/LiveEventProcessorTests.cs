using FolderTrail.Core.Features.Monitoring;
using FolderTrail.Core.Features.Reconciliation;
using FolderTrail.Core.Features.WatchedFolders;
using FolderTrail.Core.SharedKernel.Journal;
using FolderTrail.Core.SharedKernel.Paths;
using FolderTrail.Tests.Support;

namespace FolderTrail.Tests.Features.Monitoring;

public sealed class LiveEventProcessorTests : IAsyncLifetime
{
    private readonly TempDirectory _folder = new();
    private readonly CoreHarness _harness = new();
    private WatchedRoot _root = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private DateTimeOffset Now => _harness.Time.GetUtcNow();

    public async ValueTask InitializeAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _folder.CreateFile("existing.txt", "12345");
        _folder.CreateFile(@"reports\q1.xlsx", "q1");
        _folder.CreateFile(@"reports\q2.xlsx", "q2");
        _root = await _harness.AddRootAsync(_folder.Path);
        await _harness.Folders.SetExclusionsAsync(null, [new ExclusionRuleInput("*.tmp", true), new ExclusionRuleInput("~$*", true)], Token);
        await _harness.Reconciliation.RunAsync(_root.Id, ReconciliationTrigger.InitialBaseline, cancellationToken: Token);
        _harness.Processor.RegisterRoot(_root, await _harness.Folders.GetEffectiveMatcherAsync(_root.Id, Token));
    }

    public ValueTask DisposeAsync()
    {
        _harness.Dispose();
        _folder.Dispose();
        return ValueTask.CompletedTask;
    }

    private void Emit(RawEventKind kind, string path, string? oldPath = null, double atSeconds = 0) =>
        Assert.True(_harness.Processor.TryEnqueue(new RawFileEvent(_root.Id, kind, path, oldPath, Now.AddSeconds(atSeconds))));

    private Task FlushAsync() => _harness.Processor.DrainAsync(flushAll: true);

    [WindowsFact]
    public async Task Duplicate_change_notifications_become_one_modified_record()
    {
        var file = _folder.Combine("existing.txt");
        File.WriteAllText(file, "123456789");
        Emit(RawEventKind.Changed, file, atSeconds: 0);
        Emit(RawEventKind.Changed, file, atSeconds: 0.1);
        Emit(RawEventKind.Changed, file, atSeconds: 0.2);

        await FlushAsync();

        var entry = Assert.Single(await _harness.EventsAsync());
        Assert.Equal(ActivityType.Modified, entry.Type);
        Assert.Equal(ActivitySource.Live, entry.Source);
        Assert.Equal(3, entry.MergedCount);
        Assert.Equal(9, entry.Length);
        Assert.Equal(5, entry.PreviousLength);
        Assert.Equal(9, (await _harness.InventoryAsync(_root.Id))[WindowsPath.ToKey(file)].Length);
    }

    [WindowsFact]
    public async Task Creation_followed_by_writes_is_recorded_as_distinct_events_in_order()
    {
        var file = _folder.CreateFile("new.txt", "abc");
        Emit(RawEventKind.Created, file, atSeconds: 0);
        Emit(RawEventKind.Changed, file, atSeconds: 0.05);
        Emit(RawEventKind.Changed, file, atSeconds: 0.06);

        await FlushAsync();

        var events = (await _harness.EventsAsync()).OrderBy(e => e.Id).Select(e => e.Type).ToList();
        Assert.Equal([ActivityType.Created, ActivityType.Modified], events);
    }

    [WindowsFact]
    public async Task A_rename_records_both_paths_and_moves_the_inventory_entry()
    {
        var oldPath = _folder.Combine("existing.txt");
        var newPath = _folder.Combine("renamed.txt");
        File.Move(oldPath, newPath);
        Emit(RawEventKind.Renamed, newPath, oldPath);

        await FlushAsync();

        var entry = Assert.Single(await _harness.EventsAsync());
        Assert.Equal(ActivityType.Renamed, entry.Type);
        Assert.Equal(WindowsPath.Canonicalize(oldPath), entry.OldPath);
        Assert.Equal(WindowsPath.Canonicalize(newPath), entry.Path);
        Assert.Equal(ItemKind.File, entry.Kind);
        var keys = (await _harness.InventoryAsync(_root.Id)).Keys;
        Assert.Contains(WindowsPath.ToKey(newPath), keys);
        Assert.DoesNotContain(WindowsPath.ToKey(oldPath), keys);
    }

    [WindowsFact]
    public async Task Renaming_a_folder_moves_everything_beneath_it_so_scans_stay_quiet()
    {
        var oldPath = _folder.Combine("reports");
        var newPath = _folder.Combine("Archive 2026");
        Directory.Move(oldPath, newPath);
        Emit(RawEventKind.Renamed, newPath, oldPath);
        await FlushAsync();

        var keys = (await _harness.InventoryAsync(_root.Id)).Keys.ToList();
        Assert.Contains(WindowsPath.ToKey(Path.Combine(newPath, "q1.xlsx")), keys);
        Assert.DoesNotContain(keys, k => k.Contains(@"\REPORTS", StringComparison.Ordinal));

        var scan = await _harness.Reconciliation.RunAsync(_root.Id, ReconciliationTrigger.Periodic, cancellationToken: Token);
        Assert.Equal(0, scan.TotalChanges);
    }

    [WindowsFact]
    public async Task Deleting_a_folder_removes_its_subtree_and_keeps_previous_metadata()
    {
        var file = _folder.Combine("existing.txt");
        File.Delete(file);
        Directory.Delete(_folder.Combine("reports"), recursive: true);
        Emit(RawEventKind.Deleted, file);
        Emit(RawEventKind.Deleted, _folder.Combine("reports"));

        await FlushAsync();

        var events = await _harness.EventsAsync();
        Assert.Equal(2, events.Count);
        var fileEvent = Assert.Single(events, e => e.Name == "existing.txt");
        Assert.Equal(5, fileEvent.PreviousLength);
        Assert.Equal(ItemKind.Directory, Assert.Single(events, e => e.Name == "reports").Kind);
        Assert.Empty(await _harness.InventoryAsync(_root.Id));
    }

    [WindowsFact]
    public async Task Excluded_temp_files_are_ignored_but_the_rename_that_completes_a_safe_save_is_recorded()
    {
        var temp = _folder.CreateFile("report.docx.tmp", "draft");
        var lockFile = _folder.CreateFile("~$report.docx", "lock");
        Emit(RawEventKind.Created, temp);
        Emit(RawEventKind.Created, lockFile);
        Emit(RawEventKind.Changed, temp);
        var final = _folder.Combine("report.docx");
        File.Move(temp, final);
        Emit(RawEventKind.Renamed, final, temp, atSeconds: 0.2);

        await FlushAsync();

        var entry = Assert.Single(await _harness.EventsAsync());
        Assert.Equal(ActivityType.Renamed, entry.Type);
        Assert.Equal("report.docx", entry.Name);
        Assert.EndsWith("report.docx.tmp", entry.OldPath, StringComparison.Ordinal);
        Assert.Contains(WindowsPath.ToKey(final), (await _harness.InventoryAsync(_root.Id)).Keys);
    }

    [WindowsFact]
    public async Task A_short_lived_file_is_recorded_truthfully_and_not_left_in_the_inventory()
    {
        var ghost = _folder.Combine("ghost.txt");
        Emit(RawEventKind.Created, ghost);
        Emit(RawEventKind.Deleted, ghost, atSeconds: 0.1);

        await FlushAsync();

        Assert.Equal([ActivityType.Created, ActivityType.Deleted], (await _harness.EventsAsync()).OrderBy(e => e.Id).Select(e => e.Type));
        Assert.DoesNotContain(WindowsPath.ToKey(ghost), (await _harness.InventoryAsync(_root.Id)).Keys);
    }

    [WindowsFact]
    public async Task A_folder_moved_in_with_contents_is_indexed_without_inventing_child_events()
    {
        var moved = _folder.CreateDirectory("moved-in");
        _folder.CreateFile(@"moved-in\a.txt");
        _folder.CreateFile(@"moved-in\deep\b.txt");
        Emit(RawEventKind.Created, moved);

        await FlushAsync();

        var entry = Assert.Single(await _harness.EventsAsync());
        Assert.Equal(ItemKind.Directory, entry.Kind);
        var keys = (await _harness.InventoryAsync(_root.Id)).Keys;
        Assert.Contains(WindowsPath.ToKey(Path.Combine(moved, "deep", "b.txt")), keys);
        Assert.Equal(0, (await _harness.Reconciliation.RunAsync(_root.Id, ReconciliationTrigger.Periodic, cancellationToken: Token)).TotalChanges);
    }

    [WindowsFact]
    public async Task Folder_timestamp_notifications_are_not_recorded_as_modifications()
    {
        Emit(RawEventKind.Changed, _folder.Combine("reports"));
        await FlushAsync();
        Assert.Empty(await _harness.EventsAsync());
    }

    [WindowsFact]
    public async Task A_rename_without_its_old_path_is_reported_as_an_appearance()
    {
        var file = _folder.CreateFile("appeared.txt");
        Emit(RawEventKind.Renamed, file, oldPath: null);
        await FlushAsync();
        Assert.Equal(ActivityType.Created, Assert.Single(await _harness.EventsAsync()).Type);
    }

    [WindowsFact]
    public async Task Events_outside_the_folder_or_after_pausing_are_ignored()
    {
        Emit(RawEventKind.Created, @"C:\Somewhere\else.txt");
        _harness.Processor.DeactivateRoot(_root.Id);
        Emit(RawEventKind.Created, _folder.CreateFile("while-paused.txt"));
        await FlushAsync();
        Assert.Empty(await _harness.EventsAsync());
    }

    [WindowsFact]
    public async Task While_held_for_a_scan_events_are_reported_as_touched_and_applied_afterwards()
    {
        _harness.Processor.BeginHold(_root.Id);
        var file = _folder.CreateFile("during-scan.txt");
        Emit(RawEventKind.Created, file);
        await _harness.Processor.DrainAsync(flushAll: false);
        Assert.Empty(await _harness.EventsAsync());
        Assert.Contains(WindowsPath.ToKey(file), _harness.Processor.GetTouchedKeys(_root.Id).Subtrees);

        Assert.False(_harness.Processor.EndHold(_root.Id));
        await FlushAsync();

        Assert.Equal("during-scan.txt", Assert.Single(await _harness.EventsAsync()).Name);
    }

    [WindowsFact]
    public async Task Unicode_paths_are_recorded_exactly()
    {
        var file = _folder.CreateFile(@"Проекты\日本語 レポート (最終).txt");
        Emit(RawEventKind.Created, file);
        await FlushAsync();
        Assert.Equal(WindowsPath.Canonicalize(file), Assert.Single(await _harness.EventsAsync()).Path);
    }

    [Fact]
    public void A_full_queue_requests_a_rescan_once_instead_of_losing_events_silently()
    {
        using var harness = new CoreHarness();
        var processor = new LiveEventProcessor(harness.Applier, harness.Time, harness.Log, queueCapacity: 2);
        var requests = new List<long>();
        processor.RootNeedsRescan += (_, id) => requests.Add(id);

        var accepted = Enumerable.Range(0, 5)
            .Select(i => processor.TryEnqueue(new RawFileEvent(9, RawEventKind.Changed, $@"C:\r\{i}.txt", null, harness.Time.GetUtcNow())))
            .ToList();

        Assert.Equal([true, true, false, false, false], accepted);
        Assert.Equal([9L], requests);
    }
}
