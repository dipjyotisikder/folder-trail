using FolderTrail.Core.SharedKernel.FileSystem;
using FolderTrail.Core.SharedKernel.Paths;
using FolderTrail.Core.SharedKernel.Persistence;

namespace FolderTrail.Core.Features.WatchedFolders;

public sealed record AddFolderResult(bool Succeeded, WatchedRoot? Root, AddFolderCheck Check, IReadOnlyList<WatchedRoot> MergedRoots);

/// <summary>
/// Catalog of watched folders and their exclusions. It only changes stored state and announces the change;
/// the Monitoring feature reacts by starting, pausing or stopping the runtime watch.
/// </summary>
public sealed class WatchedFolderService(AppDatabase database, IFolderProbe probe, TimeProvider time)
{
    internal const string DefaultsSeededKey = "exclusions.defaultsSeeded";

    public event EventHandler<WatchedFolderChange>? Changed;

    public Task EnsureDefaultExclusionsAsync(CancellationToken cancellationToken = default) =>
        database.WriteAsync(
            (connection, transaction) =>
            {
                if (KeyValueStore.Get(connection, DefaultsSeededKey, transaction) is not null)
                {
                    return;
                }

                if (WatchedRootStore.ListExclusions(connection, null, transaction).Count == 0)
                {
                    WatchedRootStore.ReplaceExclusions(connection, transaction, null, SuggestedRules());
                }

                KeyValueStore.Set(connection, transaction, DefaultsSeededKey, "1");
            },
            cancellationToken);

    public static IReadOnlyList<ExclusionRuleInput> SuggestedRules() =>
        ExclusionPattern.Suggested.Select(p => new ExclusionRuleInput(p, IsEnabled: true, IsBuiltIn: true)).ToList();

    public Task<IReadOnlyList<WatchedRoot>> GetActiveAsync(CancellationToken cancellationToken = default) =>
        database.ReadAsync<IReadOnlyList<WatchedRoot>>(connection => WatchedRootStore.ListActive(connection), cancellationToken);

    public Task<IReadOnlyList<WatchedRoot>> GetAllIncludingRemovedAsync(CancellationToken cancellationToken = default) =>
        database.ReadAsync<IReadOnlyList<WatchedRoot>>(WatchedRootStore.ListAll, cancellationToken);

    public Task<WatchedRoot?> GetAsync(long rootId, CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => WatchedRootStore.Get(connection, rootId), cancellationToken);

    public async Task<AddFolderCheck> CheckAsync(string path, bool includeSubfolders, CancellationToken cancellationToken = default)
    {
        if (!WindowsPath.TryCanonicalize(path, out var canonical))
        {
            return new AddFolderCheck(AddFolderOutcome.InvalidPath, null, "This isn't a valid local or network folder path.", []);
        }

        var availability = await Task.Run(() => probe.Probe(canonical), cancellationToken).ConfigureAwait(false);
        if (!availability.IsAvailable)
        {
            return new AddFolderCheck(ToOutcome(availability.Availability), canonical, availability.Describe(canonical), []);
        }

        var roots = await GetActiveAsync(cancellationToken).ConfigureAwait(false);
        return RootOverlap.Analyze(canonical, includeSubfolders, roots);
    }

    /// <param name="mergeContainedRoots">Must be true to add a parent of existing watches; the user confirms this first.</param>
    public async Task<AddFolderResult> AddAsync(string path, bool includeSubfolders, bool mergeContainedRoots, CancellationToken cancellationToken = default)
    {
        var check = await CheckAsync(path, includeSubfolders, cancellationToken).ConfigureAwait(false);
        if (!check.CanAdd || (check.RequiresMerge && !mergeContainedRoots))
        {
            return new AddFolderResult(false, null, check, []);
        }

        var now = time.GetUtcNow();
        var (committed, root, merged) = await database.WriteAsync(
                (connection, transaction) =>
                {
                    // Re-evaluated inside the write transaction so two concurrent adds can't both succeed.
                    var current = RootOverlap.Analyze(check.CanonicalPath!, includeSubfolders, WatchedRootStore.ListActive(connection, transaction));
                    if (!current.CanAdd || (current.RequiresMerge && !mergeContainedRoots))
                    {
                        return (current, (WatchedRoot?)null, (IReadOnlyList<WatchedRoot>)[]);
                    }

                    foreach (var child in current.RelatedRoots)
                    {
                        WatchedRootStore.MarkRemoved(connection, transaction, child.Id, now);
                    }

                    var id = WatchedRootStore.Insert(connection, transaction, check.CanonicalPath!, includeSubfolders, now);
                    return (current, WatchedRootStore.Get(connection, id, transaction), current.RelatedRoots);
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (root is null)
        {
            return new AddFolderResult(false, null, committed, []);
        }

        foreach (var child in merged)
        {
            Raise(new WatchedFolderChange(WatchedFolderChangeKind.Removed, child.Id, RemovalMode.KeepHistory));
        }

        Raise(new WatchedFolderChange(WatchedFolderChangeKind.Added, root.Id));
        return new AddFolderResult(true, root, committed, merged);
    }

    public Task PauseAsync(long rootId, CancellationToken cancellationToken = default) => SetPausedAsync(rootId, true, cancellationToken);

    public Task ResumeAsync(long rootId, CancellationToken cancellationToken = default) => SetPausedAsync(rootId, false, cancellationToken);

    public Task PauseAllAsync(CancellationToken cancellationToken = default) => SetAllPausedAsync(true, cancellationToken);

    public Task ResumeAllAsync(CancellationToken cancellationToken = default) => SetAllPausedAsync(false, cancellationToken);

    public async Task RemoveAsync(long rootId, RemovalMode mode, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        await database.WriteAsync(
                (connection, transaction) =>
                {
                    if (mode == RemovalMode.DeleteHistory)
                    {
                        // Cascades to the folder's events, inventory, gaps and exclusions.
                        WatchedRootStore.Delete(connection, transaction, rootId);
                    }
                    else
                    {
                        WatchedRootStore.MarkRemoved(connection, transaction, rootId, now);
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);
        Raise(new WatchedFolderChange(WatchedFolderChangeKind.Removed, rootId, mode));
    }

    /// <param name="rootId">Null for the global list that applies to every folder.</param>
    public Task<IReadOnlyList<ExclusionRule>> GetExclusionsAsync(long? rootId, CancellationToken cancellationToken = default) =>
        database.ReadAsync<IReadOnlyList<ExclusionRule>>(connection => WatchedRootStore.ListExclusions(connection, rootId), cancellationToken);

    public async Task SetExclusionsAsync(long? rootId, IReadOnlyList<ExclusionRuleInput> rules, CancellationToken cancellationToken = default)
    {
        var invalid = rules.Select(r => (r.Pattern, Error: ExclusionPattern.Validate(r.Pattern))).FirstOrDefault(r => r.Error is not null);
        if (invalid.Error is not null)
        {
            throw new ArgumentException($"\"{invalid.Pattern}\": {invalid.Error}", nameof(rules));
        }

        var distinct = rules
            .GroupBy(r => ExclusionPattern.Normalize(r.Pattern)!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        await database.WriteAsync((connection, transaction) => WatchedRootStore.ReplaceExclusions(connection, transaction, rootId, distinct), cancellationToken)
            .ConfigureAwait(false);
        Raise(new WatchedFolderChange(WatchedFolderChangeKind.ExclusionsChanged, rootId));
    }

    public Task<ExclusionMatcher> GetEffectiveMatcherAsync(long rootId, CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => new ExclusionMatcher(WatchedRootStore.EffectivePatterns(connection, rootId)), cancellationToken);

    private async Task SetPausedAsync(long rootId, bool paused, CancellationToken cancellationToken)
    {
        var changed = await database.WriteAsync((connection, transaction) => WatchedRootStore.SetPaused(connection, transaction, rootId, paused), cancellationToken)
            .ConfigureAwait(false);
        if (changed > 0)
        {
            Raise(new WatchedFolderChange(paused ? WatchedFolderChangeKind.Paused : WatchedFolderChangeKind.Resumed, rootId));
        }
    }

    private async Task SetAllPausedAsync(bool paused, CancellationToken cancellationToken)
    {
        var affected = await database.WriteAsync(
                (connection, transaction) =>
                {
                    var ids = WatchedRootStore.ListActive(connection, transaction).Where(r => r.IsPaused != paused).Select(r => r.Id).ToList();
                    WatchedRootStore.SetPaused(connection, transaction, null, paused);
                    return ids;
                },
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var id in affected)
        {
            Raise(new WatchedFolderChange(paused ? WatchedFolderChangeKind.Paused : WatchedFolderChangeKind.Resumed, id));
        }
    }

    private void Raise(WatchedFolderChange change) => Changed?.Invoke(this, change);

    private static AddFolderOutcome ToOutcome(FolderAvailability availability) => availability switch
    {
        FolderAvailability.FolderMissing => AddFolderOutcome.FolderMissing,
        FolderAvailability.DriveUnavailable => AddFolderOutcome.DriveUnavailable,
        FolderAvailability.AccessDenied => AddFolderOutcome.AccessDenied,
        FolderAvailability.NotADirectory => AddFolderOutcome.NotADirectory,
        _ => AddFolderOutcome.Error,
    };
}
