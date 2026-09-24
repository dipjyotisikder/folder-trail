using FolderTrail.Core.Features.Reconciliation;
using FolderTrail.Core.Features.Retention;
using FolderTrail.Core.Features.WatchedFolders;
using FolderTrail.Tests.Features.WatchedFolders;
using FolderTrail.Tests.Support;

namespace FolderTrail.Tests.Features.Retention;

public sealed class RetentionServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Old_history_is_deleted_but_the_active_inventory_is_kept()
    {
        var scanner = new FakeScanner();
        using var harness = new CoreHarness(probe: new FakeProbe(), scanner: scanner);
        var root = await harness.AddRootAsync(@"C:\Data");
        scanner.Next(FakeScanner.File(@"C:\Data\a.txt"), FakeScanner.File(@"C:\Data\b.txt"));
        await harness.Reconciliation.RunAsync(root.Id, ReconciliationTrigger.InitialBaseline, cancellationToken: Token);

        var now = harness.Time.GetUtcNow();
        await WatchedFolderServiceTests.InsertEventAsync(harness, root.Id, @"C:\Data\ancient.txt", now.AddDays(-400));
        await WatchedFolderServiceTests.InsertEventAsync(harness, root.Id, @"C:\Data\old.txt", now.AddDays(-31));
        await WatchedFolderServiceTests.InsertEventAsync(harness, root.Id, @"C:\Data\recent.txt", now.AddDays(-29));
        await harness.Settings.UpdateAsync(s => s with { RetentionDays = 30 }, Token);
        var retention = new RetentionService(harness.Database, harness.Settings, harness.Time, harness.Log);

        var result = await retention.PruneAsync(Token);

        Assert.Equal(2, result.EventsDeleted);
        Assert.Equal("recent.txt", Assert.Single(await harness.EventsAsync()).Name);
        Assert.Equal(2, (await harness.InventoryAsync(root.Id)).Count);
    }

    [Fact]
    public async Task Unlimited_retention_keeps_everything()
    {
        using var harness = new CoreHarness(probe: new FakeProbe(), scanner: new FakeScanner());
        var root = await harness.AddRootAsync(@"C:\Data");
        await WatchedFolderServiceTests.InsertEventAsync(harness, root.Id, @"C:\Data\ancient.txt", harness.Time.GetUtcNow().AddYears(-5));
        await harness.Settings.UpdateAsync(s => s with { RetentionDays = null }, Token);

        var result = await new RetentionService(harness.Database, harness.Settings, harness.Time, harness.Log).PruneAsync(Token);

        Assert.Equal(0, result.EventsDeleted);
        Assert.Single(await harness.EventsAsync());
    }

    [Fact]
    public async Task Pruning_handles_more_rows_than_one_batch()
    {
        using var harness = new CoreHarness(probe: new FakeProbe(), scanner: new FakeScanner());
        var root = await harness.AddRootAsync(@"C:\Data");
        var old = harness.Time.GetUtcNow().AddDays(-200);
        await harness.Database.WriteAsync(
            (connection, transaction) => FolderTrail.Core.SharedKernel.Journal.ActivityJournal.Append(
                connection,
                transaction,
                Enumerable.Range(0, 12_000)
                    .Select(i => new FolderTrail.Core.SharedKernel.Journal.NewActivity(
                        root.Id, old, FolderTrail.Core.SharedKernel.Journal.ActivityType.Modified, FolderTrail.Core.SharedKernel.Journal.ActivitySource.Live,
                        FolderTrail.Core.SharedKernel.Journal.ItemKind.File, $@"C:\Data\f{i}.txt"))
                    .ToList()),
            Token);

        var result = await new RetentionService(harness.Database, harness.Settings, harness.Time, harness.Log).PruneAsync(Token);

        Assert.Equal(12_000, result.EventsDeleted);
    }

    [Fact]
    public async Task A_removed_folder_is_forgotten_once_its_kept_history_has_expired()
    {
        using var harness = new CoreHarness(probe: new FakeProbe(), scanner: new FakeScanner());
        var root = await harness.AddRootAsync(@"C:\Old");
        await WatchedFolderServiceTests.InsertEventAsync(harness, root.Id, @"C:\Old\x.txt", harness.Time.GetUtcNow().AddDays(-100));
        await harness.Folders.RemoveAsync(root.Id, RemovalMode.KeepHistory, Token);

        var result = await new RetentionService(harness.Database, harness.Settings, harness.Time, harness.Log).PruneAsync(Token);

        Assert.Equal(1, result.RemovedFoldersForgotten);
        Assert.Null(await harness.Folders.GetAsync(root.Id, Token));
    }
}
