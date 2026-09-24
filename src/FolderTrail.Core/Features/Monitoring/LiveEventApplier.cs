using FolderTrail.Core.Features.Reconciliation;
using FolderTrail.Core.Features.WatchedFolders;
using FolderTrail.Core.SharedKernel.FileSystem;
using FolderTrail.Core.SharedKernel.Journal;
using FolderTrail.Core.SharedKernel.Persistence;
using Microsoft.Data.Sqlite;

namespace FolderTrail.Core.Features.Monitoring;

internal sealed record RootContextSnapshot(long RootId, string RootPath, bool IncludeSubfolders, ExclusionMatcher Matcher);

public sealed record AppliedBatch(IReadOnlyList<long> RootIds, int EventCount, IReadOnlyList<long> RootsNeedingRescan);

/// <summary>
/// Turns coalesced live notifications into journal records and keeps the inventory in step, so later scans only
/// report what the watcher genuinely missed. Metadata is read here, on the processing pipeline, never on the
/// watcher callback thread, and before the write transaction starts.
/// </summary>
public sealed class LiveEventApplier(AppDatabase database, IItemInspector inspector, IFileSystemScanner scanner)
{
    /// <summary>A folder that appears already populated (moved in) is indexed up to this many items; beyond it a scan is requested.</summary>
    public const int MaxSubtreeItems = 10_000;

    private sealed record Resolved(ObservedChange Change, RootContextSnapshot Root, ItemInfo? Info, IReadOnlyCollection<InventoryEntry>? Subtree);

    internal async Task<AppliedBatch> ApplyAsync(
        IReadOnlyList<ObservedChange> batch,
        IReadOnlyDictionary<long, RootContextSnapshot> roots,
        CancellationToken cancellationToken)
    {
        var rescans = new HashSet<long>();
        var resolved = await Task.Run(() => Resolve(batch, roots, rescans), cancellationToken).ConfigureAwait(false);
        var (rootIds, count) = await database.WriteAsync((connection, transaction) => Write(connection, transaction, resolved), CancellationToken.None)
            .ConfigureAwait(false);
        return new AppliedBatch(rootIds, count, rescans.ToList());
    }

    private List<Resolved> Resolve(IReadOnlyList<ObservedChange> batch, IReadOnlyDictionary<long, RootContextSnapshot> roots, HashSet<long> rescans)
    {
        var resolved = new List<Resolved>(batch.Count);
        foreach (var change in batch)
        {
            if (!roots.TryGetValue(change.RootId, out var root))
            {
                continue;
            }

            var info = change.Type != ActivityType.Deleted && change.PathIncluded ? inspector.Inspect(change.Path) : null;
            IReadOnlyCollection<InventoryEntry>? subtree = null;
            var appearedWithContents = change.Type == ActivityType.Created || (change.Type == ActivityType.Renamed && !change.OldPathIncluded);
            if (appearedWithContents && info is { Kind: ItemKind.Directory, IsReparsePoint: false } && root.IncludeSubfolders)
            {
                subtree = ScanSubtree(change, root, rescans);
            }

            resolved.Add(new Resolved(change, root, info, subtree));
        }

        return resolved;
    }

    private IReadOnlyCollection<InventoryEntry>? ScanSubtree(ObservedChange change, RootContextSnapshot root, HashSet<long> rescans)
    {
        try
        {
            var outcome = scanner.Scan(
                new ScanRequest(root.RootPath, true, root.Matcher, StartPath: change.Path, MaxItems: MaxSubtreeItems),
                CancellationToken.None);
            if (outcome.Truncated || outcome.SkippedCount > 0)
            {
                rescans.Add(change.RootId);
            }

            return outcome.Items.Values.ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or RootUnavailableException)
        {
            rescans.Add(change.RootId);
            return null;
        }
    }

    private static (IReadOnlyList<long> RootIds, int Count) Write(SqliteConnection connection, SqliteTransaction transaction, List<Resolved> items)
    {
        var active = WatchedRootStore.ActiveIds(connection, transaction);
        var events = new List<NewActivity>(items.Count);
        var lastEvent = new Dictionary<long, DateTimeOffset>();

        foreach (var item in items)
        {
            if (!active.Contains(item.Change.RootId))
            {
                continue;
            }

            var activity = Apply(connection, transaction, item);
            if (activity is null)
            {
                continue;
            }

            events.Add(activity);
            lastEvent[activity.RootId] = lastEvent.TryGetValue(activity.RootId, out var existing) && existing > activity.DetectedUtc
                ? existing
                : activity.DetectedUtc;
        }

        ActivityJournal.Append(connection, transaction, events);
        foreach (var (rootId, utc) in lastEvent)
        {
            WatchedRootStore.RecordLastEvent(connection, transaction, rootId, utc);
        }

        return (lastEvent.Keys.ToList(), events.Count);
    }

    private static NewActivity? Apply(SqliteConnection connection, SqliteTransaction transaction, Resolved item)
    {
        var change = item.Change;
        var rootId = change.RootId;
        var info = item.Info;
        var detected = change.DetectedUtc;

        switch (change.Type)
        {
            case ActivityType.Created:
            {
                var previous = InventoryStore.Get(connection, transaction, rootId, change.PathKey);
                Track(connection, transaction, rootId, change.Path, info, detected, item.Subtree);
                return new NewActivity(
                    rootId, detected, ActivityType.Created, ActivitySource.Live, info?.Kind ?? previous?.Kind ?? ItemKind.Unknown, change.Path,
                    Length: info?.Length, LastWriteUtc: info?.LastWriteUtc, MergedCount: change.MergedCount);
            }

            case ActivityType.Modified:
            {
                var previous = InventoryStore.Get(connection, transaction, rootId, change.PathKey);
                var kind = info?.Kind ?? previous?.Kind ?? ItemKind.Unknown;
                if (kind == ItemKind.Directory)
                {
                    // A folder's timestamp changes whenever its contents do; the contents' own events carry that news.
                    return null;
                }

                Track(connection, transaction, rootId, change.Path, info, detected, null);
                return new NewActivity(
                    rootId, detected, ActivityType.Modified, ActivitySource.Live, kind, change.Path,
                    Length: info?.Length,
                    LastWriteUtc: info?.LastWriteUtc,
                    PreviousLength: previous?.Length,
                    PreviousLastWriteUtc: previous?.LastWriteUtc,
                    PreviousObservedUtc: previous?.RecordedUtc,
                    MergedCount: change.MergedCount);
            }

            case ActivityType.Deleted:
            {
                var previous = InventoryStore.Get(connection, transaction, rootId, change.PathKey);
                InventoryStore.DeleteSubtree(connection, transaction, rootId, change.PathKey);
                return new NewActivity(
                    rootId, detected, ActivityType.Deleted, ActivitySource.Live, previous?.Kind ?? ItemKind.Unknown, change.Path,
                    PreviousLength: previous?.Length,
                    PreviousLastWriteUtc: previous?.LastWriteUtc,
                    PreviousObservedUtc: previous?.RecordedUtc,
                    MergedCount: change.MergedCount);
            }

            case ActivityType.Renamed:
            {
                var previous = change.OldPathKey is null ? null : InventoryStore.Get(connection, transaction, rootId, change.OldPathKey);
                if (change.OldPath is not null && change.OldPathIncluded)
                {
                    if (change.PathIncluded)
                    {
                        InventoryStore.MoveSubtree(connection, transaction, rootId, change.OldPath, change.Path);
                    }
                    else
                    {
                        InventoryStore.DeleteSubtree(connection, transaction, rootId, change.OldPathKey!);
                    }
                }

                if (change.PathIncluded)
                {
                    Track(connection, transaction, rootId, change.Path, info, detected, item.Subtree);
                }

                return new NewActivity(
                    rootId, detected, ActivityType.Renamed, ActivitySource.Live, info?.Kind ?? previous?.Kind ?? ItemKind.Unknown, change.Path,
                    OldPath: change.OldPath,
                    Length: info?.Length ?? previous?.Length,
                    LastWriteUtc: info?.LastWriteUtc ?? previous?.LastWriteUtc,
                    MergedCount: change.MergedCount);
            }

            default:
                return null;
        }
    }

    /// <summary>Records the item's current state, or forgets it when it's already gone again (a short-lived temp file).</summary>
    private static void Track(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rootId,
        string path,
        ItemInfo? info,
        DateTimeOffset recordedUtc,
        IReadOnlyCollection<InventoryEntry>? subtree)
    {
        if (info is null)
        {
            InventoryStore.DeleteSubtree(connection, transaction, rootId, SharedKernel.Paths.WindowsPath.ToKey(path));
            return;
        }

        InventoryStore.Upsert(connection, transaction, rootId, [InventoryEntry.Create(path, info.Kind, info.Length, info.LastWriteUtc, recordedUtc)]);
        if (subtree is { Count: > 0 })
        {
            InventoryStore.Upsert(connection, transaction, rootId, subtree);
        }
    }
}
