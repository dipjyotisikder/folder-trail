using FolderTrail.Core.Features.Monitoring;
using FolderTrail.Core.Features.Reconciliation;
using FolderTrail.Core.Features.Settings;
using FolderTrail.Core.Features.Timeline;
using FolderTrail.Core.Features.WatchedFolders;
using FolderTrail.Core.SharedKernel.Diagnostics;
using FolderTrail.Core.SharedKernel.FileSystem;
using FolderTrail.Core.SharedKernel.Journal;
using FolderTrail.Core.SharedKernel.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace FolderTrail.Tests.Support;

/// <summary>Wires the Core features the same way the app's composition root does, with swappable edges.</summary>
public sealed class CoreHarness : IDisposable
{
    private readonly TestDatabase _database;

    public CoreHarness(
        IFolderProbe? probe = null,
        IFileSystemScanner? scanner = null,
        IDirectoryWatcherFactory? watchers = null,
        MonitoringOptions? options = null,
        bool realWatchers = false)
    {
        _database = new TestDatabase();
        Probe = probe ?? new LocalFolderProbe();
        Log = new DiagnosticLog(Time, logDirectory: null);
        KeyValues = new KeyValueStore(Database);
        Startup = new FakeStartupRegistration();
        Settings = new SettingsService(KeyValues, Startup);
        Folders = new WatchedFolderService(Database, Probe, Time);
        Scanner = scanner ?? new FileSystemScanner(Probe, Time);
        Reconciliation = new ReconciliationService(Database, Folders, Scanner, Probe, Time, Log);
        Applier = new LiveEventApplier(Database, new LocalItemInspector(), Scanner);
        Processor = new LiveEventProcessor(Applier, Time, Log) { PollInterval = TimeSpan.FromMilliseconds(20) };
        Gaps = new MonitoringGapStore(Database);
        Watchers = watchers ?? (realWatchers ? new FileSystemWatcherFactory(Time) : new FakeWatcherFactory());
        Timeline = new TimelineService(Database);
        Monitoring = new MonitoringCoordinator(
            Folders, Reconciliation, Processor, Watchers, Probe, Gaps, Settings, KeyValues, Database, Time, Log,
            options ?? new MonitoringOptions { TimerResolution = TimeSpan.FromSeconds(1) });
    }

    public FakeTimeProvider Time => _database.Time;

    public AppDatabase Database => _database.Database;

    public IFolderProbe Probe { get; }

    public DiagnosticLog Log { get; }

    public KeyValueStore KeyValues { get; }

    public FakeStartupRegistration Startup { get; }

    public SettingsService Settings { get; }

    public WatchedFolderService Folders { get; }

    public IFileSystemScanner Scanner { get; }

    public ReconciliationService Reconciliation { get; }

    public LiveEventApplier Applier { get; }

    public LiveEventProcessor Processor { get; }

    public MonitoringGapStore Gaps { get; }

    public IDirectoryWatcherFactory Watchers { get; }

    public TimelineService Timeline { get; }

    public MonitoringCoordinator Monitoring { get; }

    public async Task<WatchedRoot> AddRootAsync(string path, bool includeSubfolders = true)
    {
        var result = await Folders.AddAsync(path, includeSubfolders, mergeContainedRoots: false, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.Check.Message);
        return result.Root!;
    }

    public async Task<IReadOnlyList<TimelineEntry>> EventsAsync(TimelineFilter? filter = null) =>
        (await Timeline.GetPageAsync(filter ?? TimelineFilter.All, null, 10_000, TestContext.Current.CancellationToken)).Items;

    public Task<Dictionary<string, InventoryEntry>> InventoryAsync(long rootId) =>
        Database.ReadAsync(connection => InventoryStore.LoadRoot(connection, rootId), TestContext.Current.CancellationToken);

    public async Task<IReadOnlyList<TimelineEntry>> EventsOfTypeAsync(ActivityType type) =>
        (await EventsAsync()).Where(e => e.Type == type).ToList();

    public void Dispose()
    {
        Monitoring.StopAsync().GetAwaiter().GetResult();
        _database.Dispose();
    }
}

public sealed class FakeStartupRegistration : IStartupRegistration
{
    public bool Enabled { get; private set; }

    public bool IsSupported => true;

    public bool IsEnabled() => Enabled;

    public void SetEnabled(bool enabled) => Enabled = enabled;
}

public sealed class FakeProbe(FolderAvailability availability = FolderAvailability.Available) : IFolderProbe
{
    public FolderAvailability Availability { get; set; } = availability;

    public FolderProbeResult Probe(string canonicalPath) =>
        Availability == FolderAvailability.Available ? FolderProbeResult.Available : new FolderProbeResult(Availability);
}

public sealed class FakeWatcherFactory : IDirectoryWatcherFactory
{
    private readonly object _gate = new();
    private readonly List<FakeWatcher> _created = new();

    public bool FailToStart { get; set; }

    public IReadOnlyList<FakeWatcher> Created
    {
        get
        {
            lock (_gate)
            {
                return _created.ToList();
            }
        }
    }

    public FakeWatcher? ActiveFor(long rootId) => Created.LastOrDefault(w => w.Root.Id == rootId && w.Started && !w.Disposed);

    public IDirectoryWatcher Create(WatchedRoot root, Action<RawFileEvent> onEvent, Action<WatcherFailure> onFailure)
    {
        var watcher = new FakeWatcher(root, onEvent, onFailure, FailToStart);
        lock (_gate)
        {
            _created.Add(watcher);
        }

        return watcher;
    }
}

public sealed class FakeWatcher(WatchedRoot root, Action<RawFileEvent> onEvent, Action<WatcherFailure> onFailure, bool failToStart) : IDirectoryWatcher
{
    public WatchedRoot Root { get; } = root;

    public bool Started { get; private set; }

    public bool Disposed { get; private set; }

    public void Start()
    {
        if (failToStart)
        {
            throw new IOException("Simulated watcher start failure.");
        }

        Started = true;
    }

    public void Emit(RawEventKind kind, string path, string? oldPath = null, DateTimeOffset? at = null) =>
        onEvent(new RawFileEvent(Root.Id, kind, path, oldPath, at ?? DateTimeOffset.UtcNow));

    public void Fail(bool overflow) =>
        onFailure(new WatcherFailure(Root.Id, overflow, overflow ? new InternalBufferOverflowException() : new IOException("Simulated watcher failure.")));

    public void Dispose() => Disposed = true;
}
