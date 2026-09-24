using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using FolderTrail.Core.Features.Reconciliation;
using FolderTrail.Core.Features.Settings;
using FolderTrail.Core.Features.WatchedFolders;
using FolderTrail.Core.SharedKernel.Diagnostics;
using FolderTrail.Core.SharedKernel.FileSystem;
using FolderTrail.Core.SharedKernel.Persistence;

namespace FolderTrail.Core.Features.Monitoring;

/// <summary>
/// Owns the runtime side of monitoring: one watcher per folder, scan scheduling (baseline, startup, periodic,
/// after errors, on resume, on request), monitoring gaps, availability checks and clean shutdown.
/// All runtime state is changed only by the control loop, so there are no races between watcher errors,
/// user actions, timers and finishing scans. Scans run one at a time to keep disk usage conservative.
/// </summary>
public sealed class MonitoringCoordinator : IAsyncDisposable
{
    internal const string LastRunningKey = "monitoring.lastRunningUtc";

    private readonly WatchedFolderService _folders;
    private readonly ReconciliationService _reconciliation;
    private readonly LiveEventProcessor _processor;
    private readonly IDirectoryWatcherFactory _watchers;
    private readonly IFolderProbe _probe;
    private readonly MonitoringGapStore _gaps;
    private readonly SettingsService _settings;
    private readonly KeyValueStore _keyValues;
    private readonly AppDatabase _database;
    private readonly TimeProvider _time;
    private readonly DiagnosticLog _log;
    private readonly MonitoringOptions _options;

    private readonly ConcurrentDictionary<long, RootRuntimeStatus> _statuses = new();
    private readonly Dictionary<long, RootRuntime> _runtimes = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private Channel<ControlMessage>? _control;
    private CancellationTokenSource? _stopping;
    private Task? _controlLoop;
    private Task? _processorLoop;
    private Task? _timerLoop;
    private DateTimeOffset _lastHeartbeat;
    private DateTimeOffset _lastAvailabilityCheck;
    private DateTimeOffset _lastPeriodicScan;
    private TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MonitoringCoordinator(
        WatchedFolderService folders,
        ReconciliationService reconciliation,
        LiveEventProcessor processor,
        IDirectoryWatcherFactory watchers,
        IFolderProbe probe,
        MonitoringGapStore gaps,
        SettingsService settings,
        KeyValueStore keyValues,
        AppDatabase database,
        TimeProvider time,
        DiagnosticLog log,
        MonitoringOptions options)
    {
        _folders = folders;
        _reconciliation = reconciliation;
        _processor = processor;
        _watchers = watchers;
        _probe = probe;
        _gaps = gaps;
        _settings = settings;
        _keyValues = keyValues;
        _database = database;
        _time = time;
        _log = log;
        _options = options;
    }

    public event EventHandler<RootRuntimeStatus>? StatusChanged;

    public event EventHandler<long>? RootDetached;

    /// <summary>Raised when a folder newly becomes Missing or Needs attention.</summary>
    public event EventHandler<RootRuntimeStatus>? AttentionRequired;

    public event EventHandler<ReconciliationResult>? ScanFinished;

    public event EventHandler<JournalAppendedEventArgs>? JournalAppended;

    public bool IsRunning { get; private set; }

    /// <summary>Completes once every folder known at start has been attached.</summary>
    public Task Initialized => _initialized.Task;

    public IReadOnlyList<RootRuntimeStatus> GetStatuses() =>
        _statuses.Values.OrderBy(s => s.DisplayPath, StringComparer.OrdinalIgnoreCase).ToList();

    public RootRuntimeStatus? GetStatus(long rootId) => _statuses.TryGetValue(rootId, out var status) ? status : null;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                return;
            }

            _stopping = new CancellationTokenSource();
            var token = _stopping.Token;
            _control = Channel.CreateUnbounded<ControlMessage>(new UnboundedChannelOptions { SingleReader = true });
            _initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            _folders.Changed += OnFoldersChanged;
            _settings.Changed += OnSettingsChanged;
            _processor.RootNeedsRescan += OnRootNeedsRescan;
            _processor.JournalAppended += OnJournalAppended;
            _processor.CoalescingWindow = _settings.Current.CoalescingWindow;

            var now = _time.GetUtcNow();
            _lastHeartbeat = now;
            _lastAvailabilityCheck = now;
            _lastPeriodicScan = now;

            _processorLoop = Task.Run(() => _processor.RunAsync(token), CancellationToken.None);
            _controlLoop = Task.Run(() => ControlLoopAsync(token), CancellationToken.None);
            _timerLoop = Task.Run(() => TimerLoopAsync(token), CancellationToken.None);
            IsRunning = true;
            Post(new InitializeMessage());
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Stops watchers, cancels scans, writes every queued change, and records the shutdown time.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRunning)
            {
                return;
            }

            IsRunning = false;
            _folders.Changed -= OnFoldersChanged;
            _settings.Changed -= OnSettingsChanged;
            _processor.RootNeedsRescan -= OnRootNeedsRescan;
            _processor.JournalAppended -= OnJournalAppended;

            _stopping!.Cancel();
            _control!.Writer.TryComplete();
            await AwaitQuietly(_controlLoop).ConfigureAwait(false);
            await AwaitQuietly(_timerLoop).ConfigureAwait(false);

            foreach (var runtime in _runtimes.Values)
            {
                StopWatcher(runtime);
                runtime.ScanCancellation?.Cancel();
            }

            var scans = _runtimes.Values.Select(r => r.ScanTask).OfType<Task>().ToList();
            await AwaitQuietly(Task.WhenAll(scans).WaitAsync(_options.ShutdownTimeout, CancellationToken.None)).ConfigureAwait(false);
            await AwaitQuietly(_processorLoop).ConfigureAwait(false);
            await AwaitQuietly(_processor.FlushAllAsync()).ConfigureAwait(false);

            foreach (var rootId in _runtimes.Keys)
            {
                _processor.UnregisterRoot(rootId);
            }

            _runtimes.Clear();
            _statuses.Clear();
            await AwaitQuietly(WriteHeartbeatAsync(_time.GetUtcNow())).ConfigureAwait(false);
            _initialized.TrySetResult();
            _stopping.Dispose();
            _stopping = null;
            _log.Info("Monitoring", "Monitoring stopped.");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    public void RequestScan(long? rootId = null) => Post(new ScanRequestedMessage(rootId, ReconciliationTrigger.Manual));

    /// <summary>Re-checks a folder that is missing or needs attention and resumes monitoring if possible.</summary>
    public void Retry(long rootId) => Post(new RetryMessage(rootId));

    private void Post(ControlMessage message) => _control?.Writer.TryWrite(message);

    private void OnFoldersChanged(object? sender, WatchedFolderChange change) => Post(new FolderChangedMessage(change));

    private void OnSettingsChanged(object? sender, AppSettings settings) => _processor.CoalescingWindow = settings.CoalescingWindow;

    private void OnRootNeedsRescan(object? sender, long rootId) => Post(new RescanNeededMessage(rootId));

    private void OnJournalAppended(object? sender, JournalAppendedEventArgs e)
    {
        Post(new JournalAppendedMessage(e.RootIds, _time.GetUtcNow()));
        JournalAppended?.Invoke(this, e);
    }

    private async Task ControlLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var message in _control!.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    await HandleAsync(message, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _log.Error("Monitoring", $"Handling {message.GetType().Name.Replace("Message", string.Empty, StringComparison.Ordinal)} failed.", exception);
                    if (message is InitializeMessage)
                    {
                        _initialized.TrySetResult();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task TimerLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(_options.TimerResolution, _time, token).ConfigureAwait(false);
                Post(new TickMessage());
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private Task HandleAsync(ControlMessage message, CancellationToken token) => message switch
    {
        InitializeMessage => InitializeAsync(token),
        FolderChangedMessage m => OnFolderChangedAsync(m.Change, token),
        WatcherFailedMessage m => OnWatcherFailedAsync(m.Failure, token),
        RescanNeededMessage m => OnRescanNeededAsync(m.RootId, token),
        ScanRequestedMessage m => OnScanRequestedAsync(m.RootId, m.Trigger, token),
        RetryMessage m => OnRetryAsync(m.RootId, token),
        ScanFinishedMessage m => OnScanFinishedAsync(m, token),
        JournalAppendedMessage m => OnJournalAppendedAsync(m),
        TickMessage => OnTickAsync(token),
        _ => Task.CompletedTask,
    };

    private async Task InitializeAsync(CancellationToken token)
    {
        try
        {
            var lastRunning = ParseTime(await _keyValues.GetAsync(LastRunningKey, token).ConfigureAwait(false));
            await _database.WriteAsync((connection, transaction) => InventoryStore.DeleteForRemovedRoots(connection, transaction), token).ConfigureAwait(false);
            var roots = await _folders.GetActiveAsync(token).ConfigureAwait(false);
            foreach (var root in roots)
            {
                try
                {
                    await AttachAsync(root, ReconciliationTrigger.Startup, lastRunning, token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _log.Error("Monitoring", $"Folder #{root.Id} couldn't be attached; it will be retried.", exception);
                    if (_runtimes.TryGetValue(root.Id, out var failed))
                    {
                        failed.RetryAfter = _time.GetUtcNow() + _options.ScanRetryDelay;
                        SetState(failed, RootState.NeedsAttention, "FolderTrail couldn't start monitoring this folder. It will try again shortly.");
                    }
                }
            }

            await WriteHeartbeatAsync(_time.GetUtcNow()).ConfigureAwait(false);
            _log.Info("Monitoring", $"Monitoring started for {roots.Count} folder(s).");
        }
        finally
        {
            _initialized.TrySetResult();
        }
    }

    private async Task AttachAsync(WatchedRoot root, ReconciliationTrigger trigger, DateTimeOffset? appNotRunningSince, CancellationToken token)
    {
        var matcher = await _folders.GetEffectiveMatcherAsync(root.Id, token).ConfigureAwait(false);
        var runtime = new RootRuntime(root, matcher);
        _runtimes[root.Id] = runtime;
        _processor.RegisterRoot(root, matcher);
        await RefreshGapCountAsync(runtime, token).ConfigureAwait(false);

        if (root.IsPaused)
        {
            _processor.DeactivateRoot(root.Id);
            await OpenGapAsync(runtime, GapReason.Paused, root.LastEventUtc ?? _time.GetUtcNow(), null, token).ConfigureAwait(false);
            SetState(runtime, RootState.Paused, PausedMessage);
            return;
        }

        if (appNotRunningSince is { } since && root.HasBaseline)
        {
            await OpenGapAsync(runtime, GapReason.AppNotRunning, since, null, token).ConfigureAwait(false);
        }

        await StartMonitoringAsync(runtime, trigger, token).ConfigureAwait(false);
    }

    private async Task StartMonitoringAsync(RootRuntime runtime, ReconciliationTrigger trigger, CancellationToken token)
    {
        var availability = await ProbeAsync(runtime.Root.DisplayPath, token).ConfigureAwait(false);
        if (!availability.IsAvailable)
        {
            await MarkUnavailableAsync(runtime, availability, token).ConfigureAwait(false);
            return;
        }

        runtime.Unavailable = null;
        StartWatcher(runtime);
        QueueScan(runtime, runtime.Root.HasBaseline ? trigger : ReconciliationTrigger.InitialBaseline, token);
    }

    private void StartWatcher(RootRuntime runtime)
    {
        StopWatcher(runtime);
        try
        {
            var rootId = runtime.Root.Id;
            var watcher = _watchers.Create(runtime.Root, e => _processor.TryEnqueue(e), failure => Post(new WatcherFailedMessage(failure)));
            watcher.Start();
            runtime.Watcher = watcher;
            runtime.WatcherProblem = null;
            _log.Info("Monitoring", $"Live watcher started for folder #{rootId}.");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            runtime.WatcherProblem = "Live monitoring couldn't be started for this folder. Scans still detect changes.";
            _log.Warning("Monitoring", $"The live watcher for folder #{runtime.Root.Id} couldn't start.", exception);
        }
    }

    private static void StopWatcher(RootRuntime runtime)
    {
        runtime.Watcher?.Dispose();
        runtime.Watcher = null;
    }

    private void QueueScan(RootRuntime runtime, ReconciliationTrigger trigger, CancellationToken token)
    {
        if (runtime.Root.IsPaused || runtime.Unavailable is not null)
        {
            return;
        }

        if (runtime.ScanTask is not null)
        {
            if (trigger != ReconciliationTrigger.Periodic)
            {
                runtime.PendingTrigger = runtime.PendingTrigger == ReconciliationTrigger.Manual ? ReconciliationTrigger.Manual : trigger;
            }

            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        runtime.ScanCancellation = cancellation;
        runtime.LastTrigger = trigger;
        SetState(runtime, runtime.Root.HasBaseline ? RootState.Scanning : RootState.BaselineScan, runtime.Root.HasBaseline ? null : BaselineMessage);

        var rootId = runtime.Root.Id;
        var scanToken = cancellation.Token;
        var control = _control!;
        runtime.ScanTask = Task.Run(
            async () =>
            {
                ReconciliationResult result;
                var holdOverflowed = false;
                try
                {
                    await _scanGate.WaitAsync(scanToken).ConfigureAwait(false);
                    try
                    {
                        _processor.BeginHold(rootId);
                        try
                        {
                            result = await _reconciliation.RunAsync(rootId, trigger, () => _processor.GetTouchedKeys(rootId), scanToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            holdOverflowed = _processor.EndHold(rootId);
                        }
                    }
                    finally
                    {
                        _scanGate.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    result = new ReconciliationResult(rootId, trigger, ReconciliationOutcome.Cancelled, _time.GetUtcNow());
                }
                catch (Exception exception)
                {
                    // Anything unexpected must still end the scan, or the folder would stay "Scanning" forever.
                    _log.Error("Scan", $"Scan of folder #{rootId} failed unexpectedly.", exception);
                    result = new ReconciliationResult(rootId, trigger, ReconciliationOutcome.Failed, _time.GetUtcNow(), Error: exception.Message);
                }

                control.Writer.TryWrite(new ScanFinishedMessage(rootId, result, holdOverflowed));
            },
            CancellationToken.None);
    }

    private async Task OnScanFinishedAsync(ScanFinishedMessage message, CancellationToken token)
    {
        if (!_runtimes.TryGetValue(message.RootId, out var runtime))
        {
            return;
        }

        runtime.ScanTask = null;
        runtime.ScanCancellation?.Dispose();
        runtime.ScanCancellation = null;
        var result = message.Result;
        try
        {
            await ApplyScanResultAsync(runtime, message, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Error("Monitoring", $"Recording the scan result for folder #{runtime.Root.Id} failed.", exception);
            runtime.RetryAfter = _time.GetUtcNow() + _options.ScanRetryDelay;
            SetState(runtime, RootState.NeedsAttention, "The last scan couldn't be completed. FolderTrail will try again shortly.");
        }

        ScanFinished?.Invoke(this, result);
        if (result.TotalChanges > 0)
        {
            JournalAppended?.Invoke(this, new JournalAppendedEventArgs([runtime.Root.Id], result.TotalChanges));
        }

        if (runtime.PendingTrigger is { } next && !runtime.Root.IsPaused && runtime.Unavailable is null)
        {
            runtime.PendingTrigger = null;
            QueueScan(runtime, next, token);
        }
    }

    private async Task ApplyScanResultAsync(RootRuntime runtime, ScanFinishedMessage message, CancellationToken token)
    {
        var result = message.Result;
        switch (result.Outcome)
        {
            case ReconciliationOutcome.Baseline:
            case ReconciliationOutcome.Completed:
                runtime.Root = await _folders.GetAsync(runtime.Root.Id, token).ConfigureAwait(false) ?? runtime.Root;
                runtime.ScanFailed = false;
                runtime.RetryAfter = null;
                if (runtime.Watcher is not null)
                {
                    await _gaps.CloseOpenAsync(runtime.Root.Id, result.CompletedUtc ?? _time.GetUtcNow(), token).ConfigureAwait(false);
                }

                await RefreshGapCountAsync(runtime, token).ConfigureAwait(false);
                if (message.HoldOverflowed)
                {
                    await OpenGapAsync(runtime, GapReason.EventOverflow, result.StartedUtc, null, token).ConfigureAwait(false);
                    runtime.PendingTrigger ??= ReconciliationTrigger.WatcherError;
                }

                if (runtime.Root.IsPaused)
                {
                    SetState(runtime, RootState.Paused, PausedMessage);
                }
                else
                {
                    SetSteadyState(runtime);
                }

                break;

            case ReconciliationOutcome.RootUnavailable:
                await MarkUnavailableAsync(runtime, result.Availability ?? new FolderProbeResult(FolderAvailability.Error), token).ConfigureAwait(false);
                break;

            case ReconciliationOutcome.Cancelled:
                if (!runtime.Root.IsPaused && runtime.State is RootState.Scanning or RootState.BaselineScan)
                {
                    SetSteadyState(runtime);
                }

                break;

            case ReconciliationOutcome.Failed when !runtime.Root.IsPaused:
                runtime.ScanFailed = true;
                runtime.RetryAfter = _time.GetUtcNow() + _options.ScanRetryDelay;
                SetState(runtime, RootState.NeedsAttention, "The last scan failed. FolderTrail will try again shortly.");
                break;
        }
    }

    private void SetSteadyState(RootRuntime runtime)
    {
        if (!runtime.Root.HasBaseline)
        {
            SetState(runtime, RootState.NeedsAttention, "The baseline scan didn't finish. Use Scan now to try again.");
        }
        else if (runtime.Watcher is null)
        {
            SetState(runtime, RootState.NeedsAttention, runtime.WatcherProblem ?? "Live monitoring isn't running. Scans still detect changes.");
        }
        else
        {
            SetState(runtime, RootState.Watching, runtime.Root.LastScanWarning);
        }
    }

    private async Task MarkUnavailableAsync(RootRuntime runtime, FolderProbeResult availability, CancellationToken token)
    {
        StopWatcher(runtime);
        runtime.Unavailable = availability;
        await OpenGapAsync(runtime, GapReason.PathUnavailable, _time.GetUtcNow(), availability.Availability.ToString(), token).ConfigureAwait(false);
        var state = availability.Availability is FolderAvailability.FolderMissing or FolderAvailability.DriveUnavailable
            ? RootState.Missing
            : RootState.NeedsAttention;
        if (runtime.State != state)
        {
            _log.Warning("Monitoring", $"Folder #{runtime.Root.Id} is unavailable: {availability.Availability}.");
        }

        SetState(runtime, state, availability.Describe(runtime.Root.DisplayPath));
    }

    private async Task OnFolderChangedAsync(WatchedFolderChange change, CancellationToken token)
    {
        if (change.Kind == WatchedFolderChangeKind.ExclusionsChanged)
        {
            var targets = change.RootId is { } id
                ? _runtimes.TryGetValue(id, out var single) ? [single] : []
                : _runtimes.Values.ToList();
            foreach (var target in targets)
            {
                target.Matcher = await _folders.GetEffectiveMatcherAsync(target.Root.Id, token).ConfigureAwait(false);
                _processor.UpdateExclusions(target.Root.Id, target.Matcher);
                QueueScan(target, ReconciliationTrigger.ExclusionsChanged, token);
            }

            return;
        }

        var rootId = change.RootId!.Value;
        switch (change.Kind)
        {
            case WatchedFolderChangeKind.Added:
            {
                var root = await _folders.GetAsync(rootId, token).ConfigureAwait(false);
                if (root is not null && !root.IsRemoved && !_runtimes.ContainsKey(rootId))
                {
                    _log.Info("Monitoring", $"Folder #{rootId} added.");
                    await AttachAsync(root, ReconciliationTrigger.InitialBaseline, null, token).ConfigureAwait(false);
                }

                break;
            }

            case WatchedFolderChangeKind.Removed:
                await DetachAsync(rootId, change.Removal ?? RemovalMode.KeepHistory, token).ConfigureAwait(false);
                break;

            case WatchedFolderChangeKind.Paused when _runtimes.TryGetValue(rootId, out var runtime):
                runtime.Root = await _folders.GetAsync(rootId, token).ConfigureAwait(false) ?? runtime.Root with { IsPaused = true };
                StopWatcher(runtime);
                runtime.ScanCancellation?.Cancel();
                runtime.PendingTrigger = null;
                _processor.DeactivateRoot(rootId);
                await OpenGapAsync(runtime, GapReason.Paused, _time.GetUtcNow(), null, token).ConfigureAwait(false);
                SetState(runtime, RootState.Paused, PausedMessage);
                break;

            case WatchedFolderChangeKind.Resumed when _runtimes.TryGetValue(rootId, out var runtime):
                runtime.Root = await _folders.GetAsync(rootId, token).ConfigureAwait(false) ?? runtime.Root with { IsPaused = false };
                runtime.WatcherRestarts.Clear();
                _processor.RegisterRoot(runtime.Root, runtime.Matcher);
                await StartMonitoringAsync(runtime, ReconciliationTrigger.MonitoringResumed, token).ConfigureAwait(false);
                break;
        }
    }

    private async Task DetachAsync(long rootId, RemovalMode mode, CancellationToken token)
    {
        if (_runtimes.Remove(rootId, out var runtime))
        {
            StopWatcher(runtime);
            runtime.ScanCancellation?.Cancel();
        }

        _processor.UnregisterRoot(rootId);
        _statuses.TryRemove(rootId, out _);
        if (mode == RemovalMode.KeepHistory)
        {
            var now = _time.GetUtcNow();
            await _database.WriteAsync(
                    (connection, transaction) =>
                    {
                        InventoryStore.DeleteAll(connection, transaction, rootId);
                        using var close = connection.Command(
                            "UPDATE monitoring_gaps SET end_utc = $end WHERE root_id = $root AND end_utc IS NULL;",
                            transaction);
                        close.With("$end", UnixTime.ToMs(now)).With("$root", rootId).ExecuteNonQuery();
                    },
                    token)
                .ConfigureAwait(false);
        }

        _log.Info("Monitoring", $"Folder #{rootId} removed ({(mode == RemovalMode.KeepHistory ? "history kept" : "history deleted")}).");
        RootDetached?.Invoke(this, rootId);
    }

    private async Task OnWatcherFailedAsync(WatcherFailure failure, CancellationToken token)
    {
        if (!_runtimes.TryGetValue(failure.RootId, out var runtime) || runtime.Root.IsPaused || runtime.Watcher is null)
        {
            return;
        }

        var reason = failure.IsBufferOverflow ? GapReason.EventOverflow : GapReason.WatcherError;
        await OpenGapAsync(runtime, reason, _time.GetUtcNow(), failure.Exception.GetType().Name, token).ConfigureAwait(false);
        _log.Warning(
            "Monitoring",
            failure.IsBufferOverflow
                ? $"The live watcher for folder #{runtime.Root.Id} overflowed; a scan will catch up."
                : $"The live watcher for folder #{runtime.Root.Id} reported an error.",
            failure.IsBufferOverflow ? null : failure.Exception);

        var availability = await ProbeAsync(runtime.Root.DisplayPath, token).ConfigureAwait(false);
        if (!availability.IsAvailable)
        {
            await MarkUnavailableAsync(runtime, availability, token).ConfigureAwait(false);
            return;
        }

        if (!failure.IsBufferOverflow)
        {
            var now = _time.GetUtcNow();
            runtime.WatcherRestarts.RemoveAll(t => now - t > TimeSpan.FromHours(1));
            runtime.WatcherRestarts.Add(now);
            if (runtime.WatcherRestarts.Count > _options.MaxWatcherRestartsPerHour)
            {
                StopWatcher(runtime);
                runtime.WatcherProblem = "Live monitoring keeps failing for this folder. Scans still detect changes; use Retry to restart it.";
            }
            else
            {
                StartWatcher(runtime);
            }
        }

        QueueScan(runtime, ReconciliationTrigger.WatcherError, token);
    }

    private async Task OnRescanNeededAsync(long rootId, CancellationToken token)
    {
        if (_runtimes.TryGetValue(rootId, out var runtime) && !runtime.Root.IsPaused)
        {
            await OpenGapAsync(runtime, GapReason.EventOverflow, _time.GetUtcNow(), null, token).ConfigureAwait(false);
            QueueScan(runtime, ReconciliationTrigger.WatcherError, token);
        }
    }

    private async Task OnScanRequestedAsync(long? rootId, ReconciliationTrigger trigger, CancellationToken token)
    {
        var targets = rootId is { } id
            ? _runtimes.TryGetValue(id, out var single) ? [single] : []
            : _runtimes.Values.ToList();
        foreach (var runtime in targets.Where(r => !r.Root.IsPaused))
        {
            if (runtime.Unavailable is not null || runtime.Watcher is null)
            {
                await OnRetryAsync(runtime.Root.Id, token).ConfigureAwait(false);
            }
            else
            {
                QueueScan(runtime, trigger, token);
            }
        }
    }

    private async Task OnRetryAsync(long rootId, CancellationToken token)
    {
        if (!_runtimes.TryGetValue(rootId, out var runtime) || runtime.Root.IsPaused || runtime.ScanTask is not null)
        {
            return;
        }

        runtime.WatcherRestarts.Clear();
        runtime.RetryAfter = null;
        await StartMonitoringAsync(runtime, ReconciliationTrigger.Manual, token).ConfigureAwait(false);
    }

    private Task OnJournalAppendedAsync(JournalAppendedMessage message)
    {
        foreach (var rootId in message.RootIds)
        {
            if (_runtimes.TryGetValue(rootId, out var runtime))
            {
                runtime.Root = runtime.Root with { LastEventUtc = message.AtUtc };
                Publish(runtime);
            }
        }

        return Task.CompletedTask;
    }

    private async Task OnTickAsync(CancellationToken token)
    {
        var now = _time.GetUtcNow();

        if (now - _lastHeartbeat >= _options.HeartbeatInterval)
        {
            var sinceLast = now - _lastHeartbeat;
            var suspendedFrom = _lastHeartbeat;
            _lastHeartbeat = now;
            await WriteHeartbeatAsync(now).ConfigureAwait(false);
            if (sinceLast > _options.HeartbeatInterval + _options.SuspendDetectionThreshold)
            {
                await OnSystemResumedAsync(suspendedFrom, token).ConfigureAwait(false);
            }
        }

        if (now - _lastAvailabilityCheck >= _options.AvailabilityCheckInterval)
        {
            _lastAvailabilityCheck = now;
            await CheckAvailabilityAsync(now, token).ConfigureAwait(false);
        }

        if (now - _lastPeriodicScan >= _settings.Current.PeriodicScanInterval)
        {
            _lastPeriodicScan = now;
            foreach (var runtime in _runtimes.Values.Where(r => !r.Root.IsPaused && r.Unavailable is null && r.Root.HasBaseline && r.ScanTask is null))
            {
                QueueScan(runtime, ReconciliationTrigger.Periodic, token);
            }
        }
    }

    private async Task OnSystemResumedAsync(DateTimeOffset suspendedFrom, CancellationToken token)
    {
        _log.Info("Monitoring", "The computer appears to have resumed from sleep; rescanning watched folders.");
        foreach (var runtime in _runtimes.Values.Where(r => !r.Root.IsPaused).ToList())
        {
            if (runtime.Root.HasBaseline)
            {
                await OpenGapAsync(runtime, GapReason.SystemSuspended, suspendedFrom, null, token).ConfigureAwait(false);
            }

            if (runtime.ScanTask is null)
            {
                await StartMonitoringAsync(runtime, ReconciliationTrigger.SystemResumed, token).ConfigureAwait(false);
            }
            else
            {
                runtime.PendingTrigger = ReconciliationTrigger.SystemResumed;
            }
        }
    }

    private async Task CheckAvailabilityAsync(DateTimeOffset now, CancellationToken token)
    {
        var candidates = _runtimes.Values.Where(r => !r.Root.IsPaused && r.ScanTask is null).ToList();
        var probes = await Task.WhenAll(candidates.Select(async r => (Runtime: r, Result: await ProbeAsync(r.Root.DisplayPath, token).ConfigureAwait(false))))
            .ConfigureAwait(false);

        foreach (var (runtime, availability) in probes)
        {
            if (!_runtimes.ContainsKey(runtime.Root.Id) || runtime.Root.IsPaused || runtime.ScanTask is not null)
            {
                continue;
            }

            if (runtime.Unavailable is not null)
            {
                if (availability.IsAvailable)
                {
                    _log.Info("Monitoring", $"Folder #{runtime.Root.Id} is available again.");
                    await StartMonitoringAsync(runtime, ReconciliationTrigger.PathAvailableAgain, token).ConfigureAwait(false);
                }
                else if (availability.Availability != runtime.Unavailable.Availability)
                {
                    await MarkUnavailableAsync(runtime, availability, token).ConfigureAwait(false);
                }

                continue;
            }

            if (!availability.IsAvailable)
            {
                // Catches folders that were moved, renamed, deleted or disconnected while being watched.
                await MarkUnavailableAsync(runtime, availability, token).ConfigureAwait(false);
                continue;
            }

            if (runtime.RetryAfter is { } retryAt && now >= retryAt)
            {
                runtime.RetryAfter = null;
                QueueScan(runtime, runtime.LastTrigger ?? ReconciliationTrigger.Periodic, token);
            }
        }
    }

    private async Task<FolderProbeResult> ProbeAsync(string path, CancellationToken token)
    {
        try
        {
            return await Task.Run(() => _probe.Probe(path), token).WaitAsync(_options.ProbeTimeout, token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new FolderProbeResult(FolderAvailability.DriveUnavailable, "The location didn't respond.");
        }
    }

    private async Task OpenGapAsync(RootRuntime runtime, GapReason reason, DateTimeOffset? start, string? detail, CancellationToken token)
    {
        if (await _gaps.OpenAsync(runtime.Root.Id, reason, start, detail, token).ConfigureAwait(false))
        {
            await RefreshGapCountAsync(runtime, token).ConfigureAwait(false);
        }
    }

    private async Task RefreshGapCountAsync(RootRuntime runtime, CancellationToken token)
    {
        runtime.OpenGapCount = await _gaps.CountOpenAsync(runtime.Root.Id, token).ConfigureAwait(false);
        if (_statuses.ContainsKey(runtime.Root.Id))
        {
            Publish(runtime);
        }
    }

    private Task WriteHeartbeatAsync(DateTimeOffset now) =>
        _keyValues.SetAsync(LastRunningKey, UnixTime.ToMs(now).ToString(CultureInfo.InvariantCulture));

    private void SetState(RootRuntime runtime, RootState state, string? message)
    {
        var previous = runtime.State;
        var wasPublished = _statuses.ContainsKey(runtime.Root.Id);
        runtime.State = state;
        runtime.Message = message;
        var status = Publish(runtime);
        if (state is RootState.Missing or RootState.NeedsAttention && (previous != state || !wasPublished))
        {
            AttentionRequired?.Invoke(this, status);
        }
    }

    private RootRuntimeStatus Publish(RootRuntime runtime)
    {
        var root = runtime.Root;
        var status = new RootRuntimeStatus(
            root.Id,
            root.DisplayPath,
            root.Name,
            root.IncludeSubfolders,
            runtime.State,
            runtime.Message,
            runtime.Watcher is not null,
            root.AddedUtc,
            root.BaselineCompletedUtc,
            root.BaselineItemCount,
            root.LastSuccessfulScanUtc,
            root.LastEventUtc,
            root.LastScanSkippedCount,
            runtime.OpenGapCount);
        _statuses[root.Id] = status;
        StatusChanged?.Invoke(this, status);
        return status;
    }

    private static DateTimeOffset? ParseTime(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? UnixTime.FromMs(ms) : null;

    private static async Task AwaitQuietly(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
        }
    }

    private const string PausedMessage = "Monitoring is paused. When you resume, a scan records the net changes made meanwhile.";
    private const string BaselineMessage = "Indexing the folder's current contents. This creates a starting point and doesn't add activity records.";

    private sealed class RootRuntime(WatchedRoot root, ExclusionMatcher matcher)
    {
        public WatchedRoot Root { get; set; } = root;

        public ExclusionMatcher Matcher { get; set; } = matcher;

        public IDirectoryWatcher? Watcher { get; set; }

        public string? WatcherProblem { get; set; }

        public List<DateTimeOffset> WatcherRestarts { get; } = new();

        public FolderProbeResult? Unavailable { get; set; }

        public Task? ScanTask { get; set; }

        public CancellationTokenSource? ScanCancellation { get; set; }

        public ReconciliationTrigger? PendingTrigger { get; set; }

        public ReconciliationTrigger? LastTrigger { get; set; }

        public DateTimeOffset? RetryAfter { get; set; }

        public bool ScanFailed { get; set; }

        public RootState State { get; set; } = RootState.Scanning;

        public string? Message { get; set; }

        public int OpenGapCount { get; set; }
    }

    private abstract record ControlMessage;

    private sealed record InitializeMessage : ControlMessage;

    private sealed record FolderChangedMessage(WatchedFolderChange Change) : ControlMessage;

    private sealed record WatcherFailedMessage(WatcherFailure Failure) : ControlMessage;

    private sealed record RescanNeededMessage(long RootId) : ControlMessage;

    private sealed record ScanRequestedMessage(long? RootId, ReconciliationTrigger Trigger) : ControlMessage;

    private sealed record RetryMessage(long RootId) : ControlMessage;

    private sealed record ScanFinishedMessage(long RootId, ReconciliationResult Result, bool HoldOverflowed) : ControlMessage;

    private sealed record JournalAppendedMessage(IReadOnlyList<long> RootIds, DateTimeOffset AtUtc) : ControlMessage;

    private sealed record TickMessage : ControlMessage;
}
