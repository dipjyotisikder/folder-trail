namespace FolderTrail.Core.Features.Monitoring;

public enum RootState
{
    BaselineScan,
    Watching,
    Scanning,
    Paused,
    Missing,
    NeedsAttention,
}

/// <summary>
/// Runtime view of one watched folder. <see cref="IsLiveWatcherActive"/> describes the watcher's health only;
/// it never implies that every change was captured.
/// </summary>
public sealed record RootRuntimeStatus(
    long RootId,
    string DisplayPath,
    string Name,
    bool IncludeSubfolders,
    RootState State,
    string? Message,
    bool IsLiveWatcherActive,
    DateTimeOffset AddedUtc,
    DateTimeOffset? BaselineCompletedUtc,
    long? BaselineItemCount,
    DateTimeOffset? LastSuccessfulScanUtc,
    DateTimeOffset? LastEventUtc,
    int LastScanSkippedCount,
    int OpenGapCount)
{
    public static string Describe(RootState state) => state switch
    {
        RootState.BaselineScan => "Baseline scan",
        RootState.Watching => "Watching",
        RootState.Scanning => "Scanning",
        RootState.Paused => "Paused",
        RootState.Missing => "Missing",
        _ => "Needs attention",
    };
}

public sealed record MonitoringOptions
{
    public TimeSpan TimerResolution { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan AvailabilityCheckInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>A heartbeat later than this beyond its interval means the machine was suspended.</summary>
    public TimeSpan SuspendDetectionThreshold { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ScanRetryDelay { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public int MaxWatcherRestartsPerHour { get; init; } = 5;
}
