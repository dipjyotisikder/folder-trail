using System.Collections.Concurrent;
using System.Threading.Channels;
using FolderTrail.Core.Features.Reconciliation;
using FolderTrail.Core.Features.WatchedFolders;
using FolderTrail.Core.SharedKernel.Diagnostics;
using FolderTrail.Core.SharedKernel.Journal;
using FolderTrail.Core.SharedKernel.Paths;

namespace FolderTrail.Core.Features.Monitoring;

public sealed class JournalAppendedEventArgs(IReadOnlyList<long> rootIds, int eventCount) : EventArgs
{
    public IReadOnlyList<long> RootIds { get; } = rootIds;

    public int EventCount { get; } = eventCount;
}

/// <summary>
/// The bounded asynchronous pipeline between watcher callbacks and the database. Callbacks only enqueue; a single
/// consumer classifies, coalesces and batches writes. While a folder is being scanned its live events are held
/// and their paths reported to the scan, so the live record wins over an inferred one for the same path.
/// When the queue is full the event is not silently dropped: the folder is flagged for a reconciliation scan.
/// </summary>
public sealed class LiveEventProcessor
{
    public const int DefaultQueueCapacity = 20_000;
    public const int MaxHeldEventsPerRoot = 50_000;

    private readonly Channel<RawFileEvent> _queue;
    private readonly object _gate = new();
    private readonly Dictionary<long, RootContext> _roots = new();
    private readonly EventCoalescer _coalescer;
    private readonly List<ObservedChange> _ready = new();
    private readonly ConcurrentDictionary<long, byte> _overflowSignaled = new();
    private readonly SemaphoreSlim _drainGate = new(1, 1);
    private readonly LiveEventApplier _applier;
    private readonly TimeProvider _time;
    private readonly DiagnosticLog _log;
    private IReadOnlyList<ObservedChange> _inFlight = [];

    public LiveEventProcessor(LiveEventApplier applier, TimeProvider time, DiagnosticLog log, int queueCapacity = DefaultQueueCapacity)
    {
        _applier = applier;
        _time = time;
        _log = log;
        _coalescer = new EventCoalescer(TimeSpan.FromSeconds(1));
        _queue = Channel.CreateBounded<RawFileEvent>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>Raised when live events for a folder were lost or couldn't be written; a scan must catch up.</summary>
    public event EventHandler<long>? RootNeedsRescan;

    public event EventHandler<JournalAppendedEventArgs>? JournalAppended;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan CoalescingWindow
    {
        get
        {
            lock (_gate)
            {
                return _coalescer.Window;
            }
        }
        set
        {
            lock (_gate)
            {
                _coalescer.Window = value;
            }
        }
    }

    /// <summary>Called on the watcher thread: must stay allocation-light and never block.</summary>
    public bool TryEnqueue(RawFileEvent rawEvent)
    {
        if (_queue.Writer.TryWrite(rawEvent))
        {
            return true;
        }

        if (_overflowSignaled.TryAdd(rawEvent.RootId, 0))
        {
            RootNeedsRescan?.Invoke(this, rawEvent.RootId);
        }

        return false;
    }

    public void RegisterRoot(WatchedRoot root, ExclusionMatcher matcher)
    {
        lock (_gate)
        {
            if (_roots.TryGetValue(root.Id, out var existing))
            {
                existing.Update(root, matcher);
                existing.Active = true;
            }
            else
            {
                _roots[root.Id] = new RootContext(root, matcher);
            }
        }
    }

    public void UpdateExclusions(long rootId, ExclusionMatcher matcher)
    {
        lock (_gate)
        {
            if (_roots.TryGetValue(rootId, out var context))
            {
                context.Matcher = matcher;
            }
        }
    }

    /// <summary>Stops accepting events for a paused folder; changes already observed are still recorded.</summary>
    public void DeactivateRoot(long rootId)
    {
        lock (_gate)
        {
            if (_roots.TryGetValue(rootId, out var context))
            {
                context.Active = false;
                ReleaseHeld(context);
                _coalescer.FlushRoot(rootId, _ready);
            }
        }
    }

    public void UnregisterRoot(long rootId)
    {
        lock (_gate)
        {
            _roots.Remove(rootId);
            _coalescer.DropRoot(rootId);
            _ready.RemoveAll(c => c.RootId == rootId);
        }
    }

    public void BeginHold(long rootId)
    {
        lock (_gate)
        {
            if (!_roots.TryGetValue(rootId, out var context))
            {
                return;
            }

            context.IsHeld = true;
            context.HeldOverflowed = false;
            context.ClearTouched();
            foreach (var change in _inFlight.Concat(_ready).Where(c => c.RootId == rootId))
            {
                context.Touch(change);
            }

            // Only modifications wait in the coalescer, so they protect their exact path.
            foreach (var key in _coalescer.PendingKeys(rootId))
            {
                context.TouchedExact.Add(key);
            }
        }
    }

    public TouchedPaths GetTouchedKeys(long rootId)
    {
        lock (_gate)
        {
            // Route whatever is already queued so a notification from the scan window isn't missed.
            while (_queue.Reader.TryRead(out var rawEvent))
            {
                RouteSafely(rawEvent);
            }

            return _roots.TryGetValue(rootId, out var context)
                ? new TouchedPaths(
                    new HashSet<string>(context.TouchedSubtrees, StringComparer.Ordinal),
                    new HashSet<string>(context.TouchedExact, StringComparer.Ordinal))
                : TouchedPaths.None;
        }
    }

    /// <returns>True when so many events arrived during the hold that some were discarded (a rescan is needed).</returns>
    public bool EndHold(long rootId)
    {
        lock (_gate)
        {
            if (!_roots.TryGetValue(rootId, out var context))
            {
                return false;
            }

            var overflowed = context.HeldOverflowed;
            ReleaseHeld(context);
            context.IsHeld = false;
            context.HeldOverflowed = false;
            context.ClearTouched();
            return overflowed;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    wait.CancelAfter(PollInterval);
                    try
                    {
                        await _queue.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                    }
                }

                await DrainAsync(flushAll: false).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _log.Error("Live events", "Processing live events failed; affected folders will be rescanned.", exception);
            }
        }
    }

    /// <summary>Writes everything still queued or pending, including held events. Used at shutdown.</summary>
    public Task FlushAllAsync() => DrainAsync(flushAll: true);

    internal async Task DrainAsync(bool flushAll)
    {
        await _drainGate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<ObservedChange> batch;
            Dictionary<long, RootContextSnapshot> contexts;
            lock (_gate)
            {
                while (_queue.Reader.TryRead(out var rawEvent))
                {
                    RouteSafely(rawEvent);
                }

                _overflowSignaled.Clear();
                if (flushAll)
                {
                    foreach (var context in _roots.Values)
                    {
                        ReleaseHeld(context);
                    }

                    _coalescer.FlushAll(_ready);
                }
                else
                {
                    _coalescer.FlushDue(_time.GetUtcNow(), _ready);
                }

                if (_ready.Count == 0)
                {
                    return;
                }

                batch = _ready.OrderBy(c => c.DetectedUtc).ToList();
                _ready.Clear();
                contexts = new Dictionary<long, RootContextSnapshot>();
                foreach (var rootId in batch.Select(c => c.RootId).Distinct())
                {
                    if (_roots.TryGetValue(rootId, out var context))
                    {
                        contexts[rootId] = context.Snapshot();
                    }
                }

                foreach (var change in batch)
                {
                    if (_roots.TryGetValue(change.RootId, out var context) && context.IsHeld)
                    {
                        context.Touch(change);
                    }
                }

                _inFlight = batch;
            }

            try
            {
                var applied = await _applier.ApplyAsync(batch, contexts, CancellationToken.None).ConfigureAwait(false);
                foreach (var rootId in applied.RootsNeedingRescan)
                {
                    RootNeedsRescan?.Invoke(this, rootId);
                }

                if (applied.EventCount > 0)
                {
                    JournalAppended?.Invoke(this, new JournalAppendedEventArgs(applied.RootIds, applied.EventCount));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _log.Error("Live events", $"{batch.Count} live change(s) couldn't be saved; the affected folders will be rescanned.", exception);
                foreach (var rootId in batch.Select(c => c.RootId).Distinct())
                {
                    RootNeedsRescan?.Invoke(this, rootId);
                }
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight = [];
                }
            }
        }
        finally
        {
            _drainGate.Release();
        }
    }

    private void RouteSafely(RawFileEvent rawEvent)
    {
        try
        {
            Route(rawEvent);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            // A notification that can't be classified must not drop the rest of the batch; a scan catches up.
            _log.Warning("Live events", $"A change notification for folder #{rawEvent.RootId} couldn't be processed; the folder will be rescanned.", exception);
            RootNeedsRescan?.Invoke(this, rawEvent.RootId);
        }
    }

    private void Route(RawFileEvent rawEvent)
    {
        if (!_roots.TryGetValue(rawEvent.RootId, out var context) || !context.Active)
        {
            return;
        }

        if (!WindowsPath.TryCanonicalize(rawEvent.FullPath, out var path))
        {
            return;
        }

        var key = WindowsPath.ToKey(path);
        if (!WindowsPath.IsDescendant(context.RootKey, key))
        {
            return;
        }

        var included = !context.Matcher.IsExcluded(context.RootPath, path);
        string? oldPath = null;
        string? oldKey = null;
        var oldIncluded = false;
        if (rawEvent.Kind == RawEventKind.Renamed
            && WindowsPath.TryCanonicalize(rawEvent.OldFullPath, out var canonicalOld)
            && WindowsPath.IsDescendant(context.RootKey, WindowsPath.ToKey(canonicalOld)))
        {
            oldPath = canonicalOld;
            oldKey = WindowsPath.ToKey(canonicalOld);
            oldIncluded = !context.Matcher.IsExcluded(context.RootPath, canonicalOld);
        }

        var type = rawEvent.Kind switch
        {
            RawEventKind.Created => ActivityType.Created,
            RawEventKind.Changed => ActivityType.Modified,
            RawEventKind.Deleted => ActivityType.Deleted,
            _ => ActivityType.Renamed,
        };

        if (type == ActivityType.Renamed)
        {
            if (oldPath is null)
            {
                // Without the old path a rename can only be reported truthfully as an appearance.
                type = ActivityType.Created;
                if (!included)
                {
                    return;
                }
            }
            else if (!included && !oldIncluded)
            {
                return;
            }
        }
        else if (!included)
        {
            return;
        }

        var change = new ObservedChange(
            rawEvent.RootId, type, path, key, oldPath, oldKey, included, oldIncluded, rawEvent.ObservedUtc, rawEvent.ObservedUtc);

        if (context.IsHeld)
        {
            context.Touch(change);
            if (context.Held.Count >= MaxHeldEventsPerRoot)
            {
                context.HeldOverflowed = true;
                return;
            }

            context.Held.Add(change);
            return;
        }

        _coalescer.Add(change, _ready);
    }

    private void ReleaseHeld(RootContext context)
    {
        foreach (var held in context.Held)
        {
            _coalescer.Add(held, _ready);
        }

        context.Held.Clear();
    }

    private sealed class RootContext
    {
        public RootContext(WatchedRoot root, ExclusionMatcher matcher)
        {
            Update(root, matcher);
            Matcher = matcher;
        }

        public long RootId { get; private set; }

        public string RootPath { get; private set; } = string.Empty;

        public string RootKey { get; private set; } = string.Empty;

        public bool IncludeSubfolders { get; private set; }

        public ExclusionMatcher Matcher { get; set; }

        public bool Active { get; set; } = true;

        public bool IsHeld { get; set; }

        public bool HeldOverflowed { get; set; }

        public List<ObservedChange> Held { get; } = new();

        public HashSet<string> TouchedSubtrees { get; } = new(StringComparer.Ordinal);

        public HashSet<string> TouchedExact { get; } = new(StringComparer.Ordinal);

        public void ClearTouched()
        {
            TouchedSubtrees.Clear();
            TouchedExact.Clear();
        }

        public void Update(WatchedRoot root, ExclusionMatcher matcher)
        {
            RootId = root.Id;
            RootPath = root.DisplayPath;
            RootKey = root.PathKey;
            IncludeSubfolders = root.IncludeSubfolders;
            Matcher = matcher;
        }

        public void Touch(ObservedChange change)
        {
            if (change.Type == ActivityType.Modified)
            {
                TouchedExact.Add(change.PathKey);
                return;
            }

            TouchedSubtrees.Add(change.PathKey);
            if (change.OldPathKey is not null)
            {
                TouchedSubtrees.Add(change.OldPathKey);
            }
        }

        public RootContextSnapshot Snapshot() => new(RootId, RootPath, IncludeSubfolders, Matcher);
    }
}
