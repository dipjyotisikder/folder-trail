using FolderTrail.Core.Features.WatchedFolders;
using FolderTrail.Core.SharedKernel.Diagnostics;
using FolderTrail.Core.SharedKernel.FileSystem;
using FolderTrail.Core.SharedKernel.Journal;
using FolderTrail.Core.SharedKernel.Paths;
using FolderTrail.Core.SharedKernel.Persistence;
using Microsoft.Data.Sqlite;

namespace FolderTrail.Core.Features.Reconciliation;

public enum ReconciliationTrigger
{
    InitialBaseline,
    Startup,
    Periodic,
    SystemResumed,
    WatcherError,
    PathAvailableAgain,
    MonitoringResumed,
    ExclusionsChanged,
    Manual,
}

public enum ReconciliationOutcome
{
    Baseline,
    Completed,
    RootUnavailable,
    RootNotFound,
    Cancelled,
    Failed,
}

public sealed record ReconciliationResult(
    long RootId,
    ReconciliationTrigger Trigger,
    ReconciliationOutcome Outcome,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc = null,
    int ItemCount = 0,
    int Created = 0,
    int Modified = 0,
    int Deleted = 0,
    int Skipped = 0,
    int SilentlyIndexed = 0,
    FolderProbeResult? Availability = null,
    string? Error = null)
{
    public bool Succeeded => Outcome is ReconciliationOutcome.Baseline or ReconciliationOutcome.Completed;

    public int TotalChanges => Created + Modified + Deleted;
}

/// <summary>
/// Compares a fresh scan with the stored inventory and records net differences. The first scan of a folder is a
/// baseline: it fills the inventory and records no activity. Inferred records carry the scan time as their
/// detection time, because the real change time is unknown.
/// </summary>
public sealed class ReconciliationService(
    AppDatabase database,
    WatchedFolderService folders,
    IFileSystemScanner scanner,
    IFolderProbe probe,
    TimeProvider time,
    DiagnosticLog log)
{
    /// <param name="liveTouched">Returns the paths the live watcher reported while the scan ran. It's read inside the
    /// write transaction, as late as possible, so notifications that arrive during the walk are still included.</param>
    public async Task<ReconciliationResult> RunAsync(
        long rootId,
        ReconciliationTrigger trigger,
        Func<TouchedPaths>? liveTouched = null,
        CancellationToken cancellationToken = default)
    {
        var started = time.GetUtcNow();
        try
        {
            var root = await folders.GetAsync(rootId, cancellationToken).ConfigureAwait(false);
            if (root is null || root.IsRemoved)
            {
                return new ReconciliationResult(rootId, trigger, ReconciliationOutcome.RootNotFound, started);
            }

            var availability = await Task.Run(() => probe.Probe(root.DisplayPath), cancellationToken).ConfigureAwait(false);
            if (!availability.IsAvailable)
            {
                return new ReconciliationResult(rootId, trigger, ReconciliationOutcome.RootUnavailable, started, Availability: availability);
            }

            var matcher = await folders.GetEffectiveMatcherAsync(rootId, cancellationToken).ConfigureAwait(false);
            ScanOutcome scan;
            try
            {
                scan = await Task.Run(() => scanner.Scan(new ScanRequest(root.DisplayPath, root.IncludeSubfolders, matcher), cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (RootUnavailableException exception)
            {
                return new ReconciliationResult(rootId, trigger, ReconciliationOutcome.RootUnavailable, started, Availability: exception.Availability);
            }

            var source = trigger == ReconciliationTrigger.Manual ? ActivitySource.ManualScan : ActivitySource.Reconciliation;
            var result = await database.WriteAsync(
                    (connection, transaction) => Apply(
                        connection, transaction, rootId, trigger, source, matcher, scan,
                        InventoryDiffer.Protect(liveTouched?.Invoke() ?? TouchedPaths.None), started),
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (result.Succeeded)
            {
                log.Info(
                    "Scan",
                    result.Outcome == ReconciliationOutcome.Baseline
                        ? $"Baseline for folder #{rootId}: {result.ItemCount} items indexed, {result.Skipped} skipped."
                        : $"Scan of folder #{rootId} ({trigger}): {result.Created} created, {result.Modified} modified, {result.Deleted} deleted, {result.Skipped} skipped.");
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ReconciliationResult(rootId, trigger, ReconciliationOutcome.Cancelled, started);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            log.Error("Scan", $"Scan of folder #{rootId} failed.", exception);
            return new ReconciliationResult(rootId, trigger, ReconciliationOutcome.Failed, started, Error: exception.Message);
        }
    }

    private static ReconciliationResult Apply(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rootId,
        ReconciliationTrigger trigger,
        ActivitySource source,
        ExclusionMatcher matcher,
        ScanOutcome scan,
        Func<string, bool> isProtected,
        DateTimeOffset started)
    {
        var root = WatchedRootStore.Get(connection, rootId, transaction);
        if (root is null || root.IsRemoved)
        {
            return new ReconciliationResult(rootId, trigger, ReconciliationOutcome.RootNotFound, started);
        }

        var stored = InventoryStore.LoadRoot(connection, rootId, transaction);

        // A newly excluded item leaves the inventory silently: excluding something isn't deleting it.
        var nowExcluded = stored.Values.Where(e => IsOutsideOrExcluded(root, matcher, e)).Select(e => e.PathKey).ToList();
        foreach (var key in nowExcluded)
        {
            stored.Remove(key);
        }

        InventoryStore.Delete(connection, transaction, rootId, nowExcluded);
        var warning = scan.SkippedCount == 0
            ? null
            : scan.SkippedCount == 1
                ? "1 item couldn't be read during the last scan."
                : $"{scan.SkippedCount} items couldn't be read during the last scan.";

        if (!root.HasBaseline)
        {
            InventoryStore.Upsert(connection, transaction, rootId, scan.Items.Values);
            WatchedRootStore.RecordBaseline(connection, transaction, rootId, scan.CompletedUtc, scan.Items.Count);
            WatchedRootStore.RecordScan(connection, transaction, rootId, scan.CompletedUtc, scan.SkippedCount, warning);
            return new ReconciliationResult(
                rootId, trigger, ReconciliationOutcome.Baseline, started, scan.CompletedUtc, scan.Items.Count, Skipped: scan.SkippedCount);
        }

        var changes = InventoryDiffer.Compare(stored, scan.Items, scan.UnreadableFolderKeys, isProtected);
        var events = new List<NewActivity>();
        var upserts = new List<InventoryEntry>();
        var deletes = new List<string>();
        int created = 0, modified = 0, deleted = 0, silentlyIndexed = 0;

        foreach (var change in changes)
        {
            switch (change.Type)
            {
                case ActivityType.Created:
                {
                    var current = change.Current!;
                    upserts.Add(current);
                    if (trigger == ReconciliationTrigger.ExclusionsChanged)
                    {
                        // Items that just stopped being excluded aren't new, so they're indexed without a record.
                        silentlyIndexed++;
                        break;
                    }

                    events.Add(new NewActivity(
                        rootId, scan.CompletedUtc, ActivityType.Created, source, current.Kind, current.DisplayPath,
                        Length: current.Length, LastWriteUtc: current.LastWriteUtc));
                    created++;
                    break;
                }

                case ActivityType.Deleted:
                {
                    var previous = change.Previous!;
                    deletes.Add(previous.PathKey);
                    events.Add(new NewActivity(
                        rootId, scan.CompletedUtc, ActivityType.Deleted, source, previous.Kind, previous.DisplayPath,
                        PreviousLength: previous.Length,
                        PreviousLastWriteUtc: previous.LastWriteUtc,
                        PreviousObservedUtc: LastConfirmed(previous, root)));
                    deleted++;
                    break;
                }

                case ActivityType.Modified:
                {
                    var previous = change.Previous!;
                    var current = change.Current!;
                    upserts.Add(current);
                    events.Add(new NewActivity(
                        rootId, scan.CompletedUtc, ActivityType.Modified, source, current.Kind, current.DisplayPath,
                        Length: current.Length,
                        LastWriteUtc: current.LastWriteUtc,
                        PreviousLength: previous.Length,
                        PreviousLastWriteUtc: previous.LastWriteUtc,
                        PreviousObservedUtc: LastConfirmed(previous, root)));
                    modified++;
                    break;
                }
            }
        }

        InventoryStore.Delete(connection, transaction, rootId, deletes);
        InventoryStore.Upsert(connection, transaction, rootId, upserts);
        ActivityJournal.Append(connection, transaction, events);
        WatchedRootStore.RecordScan(connection, transaction, rootId, scan.CompletedUtc, scan.SkippedCount, warning);
        if (events.Count > 0)
        {
            WatchedRootStore.RecordLastEvent(connection, transaction, rootId, scan.CompletedUtc);
        }

        return new ReconciliationResult(
            rootId, trigger, ReconciliationOutcome.Completed, started, scan.CompletedUtc, scan.Items.Count,
            created, modified, deleted, scan.SkippedCount, silentlyIndexed);
    }

    /// <summary>The last time this metadata was known to be accurate: when recorded, or the last scan that saw it unchanged.</summary>
    private static DateTimeOffset LastConfirmed(InventoryEntry entry, WatchedRoot root) =>
        root.LastSuccessfulScanUtc is { } lastScan && lastScan > entry.RecordedUtc ? lastScan : entry.RecordedUtc;

    private static bool IsOutsideOrExcluded(WatchedRoot root, ExclusionMatcher matcher, InventoryEntry entry) =>
        !WindowsPath.IsDescendant(root.PathKey, entry.PathKey) || matcher.IsExcluded(root.DisplayPath, entry.DisplayPath);
}
