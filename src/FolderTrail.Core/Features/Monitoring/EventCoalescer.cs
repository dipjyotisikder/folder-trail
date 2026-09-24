using FolderTrail.Core.SharedKernel.Journal;

namespace FolderTrail.Core.Features.Monitoring;

/// <summary>A watcher notification after path canonicalization and exclusion checks.</summary>
/// <param name="PathIncluded">False only for the new side of a rename into an excluded name.</param>
/// <param name="OldPathIncluded">False when a rename's old side was excluded (or absent).</param>
public sealed record ObservedChange(
    long RootId,
    ActivityType Type,
    string Path,
    string PathKey,
    string? OldPath,
    string? OldPathKey,
    bool PathIncluded,
    bool OldPathIncluded,
    DateTimeOffset FirstObservedUtc,
    DateTimeOffset LastObservedUtc,
    int MergedCount = 1)
{
    /// <summary>A merged burst of modifications is dated by its last notification; everything else by its only one.</summary>
    public DateTimeOffset DetectedUtc => Type == ActivityType.Modified ? LastObservedUtc : FirstObservedUtc;
}

/// <summary>
/// Collapses notification noise without hiding real activity. Repeated modifications of one path within the
/// window become a single record (held until the path is quiet, or at most <see cref="MaxHold"/>). A repeated
/// identical Created/Deleted/Renamed notification within the window is a duplicate and dropped. Different event
/// types are never merged, and any other event for a path first releases that path's pending modification so
/// order is preserved (for example the delete/rename sequence of an application's safe save).
/// </summary>
public sealed class EventCoalescer(TimeSpan window)
{
    private readonly Dictionary<(long RootId, string Key), ObservedChange> _pending = new();
    private readonly Dictionary<(long RootId, string Key), ObservedChange> _lastEmitted = new();

    public TimeSpan Window { get; set; } = window;

    public TimeSpan MaxHold => Window * 10 > TimeSpan.FromSeconds(5) ? Window * 10 : TimeSpan.FromSeconds(5);

    public int PendingCount => _pending.Count;

    public void Add(ObservedChange change, ICollection<ObservedChange> output)
    {
        var key = (change.RootId, change.PathKey);
        if (change.Type == ActivityType.Modified)
        {
            if (_pending.TryGetValue(key, out var pending))
            {
                var merged = pending with { LastObservedUtc = change.LastObservedUtc, MergedCount = pending.MergedCount + change.MergedCount };
                if (merged.LastObservedUtc - merged.FirstObservedUtc >= MaxHold)
                {
                    _pending.Remove(key);
                    Emit(merged, output);
                }
                else
                {
                    _pending[key] = merged;
                }

                return;
            }

            _pending[key] = change;
            return;
        }

        Release(key, output);
        if (change.OldPathKey is not null)
        {
            Release((change.RootId, change.OldPathKey), output);
        }

        if (_lastEmitted.TryGetValue(key, out var last)
            && last.Type == change.Type
            && string.Equals(last.OldPathKey, change.OldPathKey, StringComparison.Ordinal)
            && change.FirstObservedUtc - last.DetectedUtc <= Window)
        {
            return;
        }

        Emit(change, output);
    }

    public void FlushDue(DateTimeOffset now, ICollection<ObservedChange> output)
    {
        foreach (var (key, pending) in _pending.ToList())
        {
            if (now - pending.LastObservedUtc >= Window)
            {
                _pending.Remove(key);
                Emit(pending, output);
            }
        }

        foreach (var (key, last) in _lastEmitted.ToList())
        {
            if (now - last.DetectedUtc > Window)
            {
                _lastEmitted.Remove(key);
            }
        }
    }

    public void FlushAll(ICollection<ObservedChange> output)
    {
        foreach (var pending in _pending.Values.OrderBy(p => p.LastObservedUtc))
        {
            output.Add(pending);
        }

        _pending.Clear();
        _lastEmitted.Clear();
    }

    public void FlushRoot(long rootId, ICollection<ObservedChange> output)
    {
        foreach (var (key, pending) in _pending.Where(p => p.Key.RootId == rootId).OrderBy(p => p.Value.LastObservedUtc).ToList())
        {
            _pending.Remove(key);
            output.Add(pending);
        }

        ForgetEmitted(rootId);
    }

    public void DropRoot(long rootId)
    {
        foreach (var key in _pending.Keys.Where(k => k.RootId == rootId).ToList())
        {
            _pending.Remove(key);
        }

        ForgetEmitted(rootId);
    }

    public IEnumerable<string> PendingKeys(long rootId) => _pending.Keys.Where(k => k.RootId == rootId).Select(k => k.Key);

    private void Release((long RootId, string Key) key, ICollection<ObservedChange> output)
    {
        if (_pending.Remove(key, out var pending))
        {
            Emit(pending, output);
        }
    }

    private void Emit(ObservedChange change, ICollection<ObservedChange> output)
    {
        output.Add(change);
        _lastEmitted[(change.RootId, change.PathKey)] = change;
    }

    private void ForgetEmitted(long rootId)
    {
        foreach (var key in _lastEmitted.Keys.Where(k => k.RootId == rootId).ToList())
        {
            _lastEmitted.Remove(key);
        }
    }
}
