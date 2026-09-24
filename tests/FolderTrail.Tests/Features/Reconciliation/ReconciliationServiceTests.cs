using FolderTrail.Core.Features.Reconciliation;
using FolderTrail.Core.Features.WatchedFolders;
using FolderTrail.Core.SharedKernel.FileSystem;
using FolderTrail.Core.SharedKernel.Journal;
using FolderTrail.Core.SharedKernel.Paths;
using FolderTrail.Tests.Support;

namespace FolderTrail.Tests.Features.Reconciliation;

public sealed class ReconciliationServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [WindowsFact]
    public async Task Baseline_indexes_existing_items_without_creating_activity()
    {
        using var folder = new TempDirectory();
        for (var i = 0; i < 30; i++)
        {
            folder.CreateFile($@"docs\sub{i % 3}\file{i}.txt");
        }

        folder.CreateFile(@"bin\ignored.dll");
        using var harness = new CoreHarness();
        await harness.Folders.EnsureDefaultExclusionsAsync(Token);
        var root = await harness.AddRootAsync(folder.Path);

        var result = await harness.Reconciliation.RunAsync(root.Id, ReconciliationTrigger.InitialBaseline, cancellationToken: Token);

        Assert.Equal(ReconciliationOutcome.Baseline, result.Outcome);
        Assert.Equal(30 + 3 + 1, result.ItemCount);
        Assert.Empty(await harness.EventsAsync());
        var inventory = await harness.InventoryAsync(root.Id);
        Assert.Equal(34, inventory.Count);
        Assert.DoesNotContain(inventory.Keys, k => k.Contains(@"\BIN", StringComparison.Ordinal));
        var stored = await harness.Folders.GetAsync(root.Id, Token);
        Assert.Equal(34, stored!.BaselineItemCount);
        Assert.NotNull(stored.LastSuccessfulScanUtc);
    }

    [WindowsFact]
    public async Task Missed_changes_are_recorded_as_net_differences_dated_by_the_scan()
    {
        using var folder = new TempDirectory();
        var baseTime = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
        folder.CreateFile("keep.txt", "same", baseTime);
        var grow = folder.CreateFile("grow.txt", "small", baseTime);
        var gone = folder.CreateFile("gone.txt", "bye", baseTime);
        var before = folder.CreateFile("before.txt", "moving", baseTime);
        using var harness = new CoreHarness();
        var root = await harness.AddRootAsync(folder.Path);
        await harness.Reconciliation.RunAsync(root.Id, ReconciliationTrigger.InitialBaseline, cancellationToken: Token);

        File.WriteAllText(grow, "much larger content now");
        File.SetLastWriteTimeUtc(grow, baseTime.AddHours(2));
        File.Delete(gone);
        File.Move(before, folder.Combine("after.txt"));
        folder.CreateFile("brand-new.txt", "hello");
        harness.Time.Advance(TimeSpan.FromHours(3));

        var result = await harness.Reconciliation.RunAsync(root.Id, ReconciliationTrigger.Startup, cancellationToken: Token);

        Assert.Equal(ReconciliationOutcome.Completed, result.Outcome);
        Assert.Equal((2, 1, 2), (result.Created, result.Modified, result.Deleted));
        var events = await harness.EventsAsync();
        Assert.All(events, e => Assert.Equal(ActivitySource.Reconciliation, e.Source));
        Assert.All(events, e => Assert.Equal(result.CompletedUtc, e.DetectedUtc));
        Assert.DoesNotContain(events, e => e.Type == ActivityType.Renamed);

        var modified = Assert.Single(events, e => e.Type == ActivityType.Modified);
        Assert.Equal("grow.txt", modified.Name);
        Assert.Equal(5, modified.PreviousLength);
        Assert.Equal(23, modified.Length);
        Assert.Equal(new DateTimeOffset(baseTime), modified.PreviousLastWriteUtc);

        var deleted = events.Where(e => e.Type == ActivityType.Deleted).Select(e => e.Name).Order().ToList();
        Assert.Equal(["before.txt", "gone.txt"], deleted);
        Assert.All(events.Where(e => e.Type == ActivityType.Deleted), e => Assert.NotNull(e.PreviousObservedUtc));
        Assert.Equal(["after.txt", "brand-new.txt"], events.Where(e => e.Type == ActivityType.Created).Select(e => e.Name).Order());
    }

    [WindowsFact]
    public async Task Repeated_scans_of_an_unchanged_folder_add_no_history()
    {
        using var folder = new TempDirectory();
        folder.CreateFile("a.txt");
        folder.CreateFile(@"sub\b.txt");
        using var harness = new CoreHarness();
        var root = await harness.AddRootAsync(folder.Path);
        await harness.Reconciliation.RunAsync(root.Id, ReconciliationTrigger.InitialBaseline, cancellationToken: Token);
        folder.CreateFile("c.txt");
        await harness.Reconciliation.RunAsync(root.Id, ReconciliationTrigger.Periodic, cancellationToken: Token);
        var afterFirst = (await harness.EventsAsync()).Count;

        for (var i = 0; i < 3; i++)
        {
            harness.Time.Advance(TimeSpan.FromMinutes(30));
            var again = await harness.Reconciliation.RunAsync(root.Id, ReconciliationTrigger.Periodic, cancellationToken: Token);
            Assert.Equal(0, again.TotalChanges);
        }

        Assert.Equal(1, afterFirst);
        Assert.Equal(afterFirst, (await harness.EventsAsync()).Count);
    }

    [WindowsFact]
    public async Task Manual_scans_are_labelled_as_manual()
    {
        using var folder = new TempDirectory();
        using var harness = new CoreHarness();
        var root = await harness.AddRootAsync(folder.Path);
        await harness.Reconciliation.RunAsync(root.Id, ReconciliationTrigger.InitialBaseline, cancellationToken: Token);
        folder.CreateFile("new.txt");

        await harness.Reconciliation.RunAsync(root.Id, ReconciliationTrigger.Manual, cancellationToken: Token);

        Assert.Equal(ActivitySource.ManualScan, Assert.Single(await harness.EventsAsync()).Source);
    }

    [WindowsFact]
    public async Task A_missing_root_is_reported_without_inventing_deletions_or_recreating_it()
    {
        using var folder = new TempDirectory();
        var watched = folder.CreateDirectory("watched");
        folder.CreateFile(@"watched\a.txt");
        using var harness = new CoreHarness();
        var root = await harness.AddRootAsync(watched);
        await harness.Reconciliation.RunAsync(root.Id, ReconciliationTrigger.InitialBaseline, cancellationToken: Token);

        Directory.Delete(watched, recursive: true);
        var result = await harness.Reconciliation.RunAsync(root.Id, ReconciliationTrigger.Periodic, cancellationToken: Token);

        Assert.Equal(ReconciliationOutcome.RootUnavailable, result.Outcome);
        Assert.Equal(FolderAvailability.FolderMissing, result.Availability!.Availability);
        Assert.Empty(await harness.EventsAsync());
        Assert.Single(await harness.InventoryAsync(root.Id));
        Assert.False(Directory.Exists(watched));
    }

    [Fact]
    public async Task An_inaccessible_root_reports_access_denied()
    {
        var probe = new FakeProbe();
        var scanner = new FakeScanner();
        using var harness = new CoreHarness(probe: probe, scanner: scanner);
        var root = await harness.AddRootAsync(@"C:\Locked");
        probe.Availability = FolderAvailability.AccessDenied;

        var result = await harness.Reconciliation.RunAsync(root.Id, ReconciliationTrigger.InitialBaseline, cancellationToken: Token);

        Assert.Equal(ReconciliationOutcome.RootUnavailable, result.Outcome);
        Assert.Equal(FolderAvailability.AccessDenied, result.Availability!.Availability);
        Assert.Equal(0, scanner.Calls);
    }

    [Fact]
    public async Task Unreadable_subfolders_are_counted_and_do_not_cause_false_deletions()
    {
        var scanner = new FakeScanner();
        using var harness = new CoreHarness(probe: new FakeProbe(), scanner: scanner);
        var root = await harness.AddRootAsync(@"C:\Data");
        scanner.Next(FakeScanner.File(@"C:\Data\a.txt"), FakeScanner.Folder(@"C:\Data\locked"), FakeScanner.File(@"C:\Data\locked\secret.txt"));
        await harness.Reconciliation.RunAsync(root.Id, ReconciliationTrigger.InitialBaseline, cancellationToken: Token);

        scanner.Next([FakeScanner.File(@"C:\Data\a.txt"), FakeScanner.Folder(@"C:\Data\locked")], unreadable: [@"C:\DATA\LOCKED"], skipped: 1);
        var result = await harness.Reconciliation.RunAsync(root.Id, ReconciliationTrigger.Periodic, cancellationToken: Token);

        Assert.Equal(0, result.TotalChanges);
        Assert.Equal(1, result.Skipped);
        Assert.Contains(@"C:\DATA\LOCKED\SECRET.TXT", (await harness.InventoryAsync(root.Id)).Keys);
        var stored = await harness.Folders.GetAsync(root.Id, Token);
        Assert.Equal(1, stored!.LastScanSkippedCount);
        Assert.Equal("1 item couldn't be read during the last scan.", stored.LastScanWarning);
    }

    [WindowsFact]
    public async Task Changing_exclusions_updates_the_inventory_without_inventing_activity()
    {
        using var folder = new TempDirectory();
        folder.CreateFile(@"logs\today.log");
        folder.CreateFile(@"cache\blob.bin");
        folder.CreateFile("keep.txt");
        using var harness = new CoreHarness();
        var root = await harness.AddRootAsync(folder.Path);
        await harness.Folders.SetExclusionsAsync(root.Id, [new ExclusionRuleInput("cache", true)], Token);
        await harness.Reconciliation.RunAsync(root.Id, ReconciliationTrigger.InitialBaseline, cancellationToken: Token);

        await harness.Folders.SetExclusionsAsync(root.Id, [new ExclusionRuleInput("logs", true)], Token);
        var result = await harness.Reconciliation.RunAsync(root.Id, ReconciliationTrigger.ExclusionsChanged, cancellationToken: Token);

        Assert.Equal(2, result.SilentlyIndexed);
        Assert.Empty(await harness.EventsAsync());
        var keys = (await harness.InventoryAsync(root.Id)).Keys.ToList();
        Assert.DoesNotContain(keys, k => k.Contains(@"\LOGS", StringComparison.Ordinal));
        Assert.Contains(keys, k => k.EndsWith(@"\CACHE\BLOB.BIN", StringComparison.Ordinal));
    }

    [WindowsFact]
    public async Task Paths_reported_by_the_live_watcher_during_the_scan_are_not_duplicated()
    {
        using var folder = new TempDirectory();
        using var harness = new CoreHarness();
        var root = await harness.AddRootAsync(folder.Path);
        await harness.Reconciliation.RunAsync(root.Id, ReconciliationTrigger.InitialBaseline, cancellationToken: Token);
        var live = folder.CreateFile("live.txt");
        folder.CreateFile("missed.txt");

        var result = await harness.Reconciliation.RunAsync(
            root.Id,
            ReconciliationTrigger.Periodic,
            () => new TouchedPaths(new HashSet<string>([WindowsPath.ToKey(live)]), new HashSet<string>()),
            Token);

        Assert.Equal(1, result.Created);
        Assert.Equal("missed.txt", Assert.Single(await harness.EventsAsync()).Name);
    }

    [WindowsFact]
    public async Task A_non_recursive_watch_indexes_only_direct_children()
    {
        using var folder = new TempDirectory();
        folder.CreateFile("top.txt");
        folder.CreateFile(@"sub\deep.txt");
        using var harness = new CoreHarness();
        var root = await harness.AddRootAsync(folder.Path, includeSubfolders: false);

        var result = await harness.Reconciliation.RunAsync(root.Id, ReconciliationTrigger.InitialBaseline, cancellationToken: Token);

        Assert.Equal(2, result.ItemCount);
        Assert.DoesNotContain((await harness.InventoryAsync(root.Id)).Keys, k => k.EndsWith("DEEP.TXT", StringComparison.Ordinal));
    }

    [WindowsFact]
    public async Task Junctions_are_recorded_as_items_but_never_followed()
    {
        using var outside = new TempDirectory();
        outside.CreateFile("elsewhere.txt");
        using var folder = new TempDirectory();
        folder.CreateFile("own.txt");
        var junction = folder.Combine("link");
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{outside.Path}\"") { CreateNoWindow = true, UseShellExecute = false })!)
        {
            await process.WaitForExitAsync(Token);
            Assert.Equal(0, process.ExitCode);
        }

        using var harness = new CoreHarness();
        var root = await harness.AddRootAsync(folder.Path);
        await harness.Reconciliation.RunAsync(root.Id, ReconciliationTrigger.InitialBaseline, cancellationToken: Token);

        var keys = (await harness.InventoryAsync(root.Id)).Keys.ToList();
        Assert.Contains(WindowsPath.ToKey(junction), keys);
        Assert.DoesNotContain(keys, k => k.EndsWith("ELSEWHERE.TXT", StringComparison.Ordinal));
    }

    [WindowsFact]
    public async Task Unicode_names_and_paths_longer_than_max_path_are_indexed()
    {
        using var folder = new TempDirectory();
        var segment = new string('ü', 40) + "_日本語_" + new string('x', 40);
        var relative = string.Join('\\', Enumerable.Repeat(segment, 4)) + @"\Résumé final.txt";
        var full = folder.CreateFile(relative);
        Assert.True(full.Length > 260);
        using var harness = new CoreHarness();
        var root = await harness.AddRootAsync(folder.Path);

        await harness.Reconciliation.RunAsync(root.Id, ReconciliationTrigger.InitialBaseline, cancellationToken: Token);

        Assert.Contains(WindowsPath.ToKey(WindowsPath.Canonicalize(full)), (await harness.InventoryAsync(root.Id)).Keys);
    }

    [Fact]
    public async Task Cancelling_a_scan_leaves_the_inventory_untouched()
    {
        var scanner = new FakeScanner { ThrowCancellation = true };
        using var harness = new CoreHarness(probe: new FakeProbe(), scanner: scanner);
        var root = await harness.AddRootAsync(@"C:\Data");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await harness.Reconciliation.RunAsync(root.Id, ReconciliationTrigger.InitialBaseline, cancellationToken: cancellation.Token);

        Assert.Equal(ReconciliationOutcome.Cancelled, result.Outcome);
        Assert.False((await harness.Folders.GetAsync(root.Id, Token))!.HasBaseline);
    }
}
