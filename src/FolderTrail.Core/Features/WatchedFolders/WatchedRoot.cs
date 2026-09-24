using FolderTrail.Core.SharedKernel.Paths;

namespace FolderTrail.Core.Features.WatchedFolders;

public sealed record WatchedRoot(
    long Id,
    string DisplayPath,
    bool IncludeSubfolders,
    bool IsPaused,
    DateTimeOffset AddedUtc,
    DateTimeOffset? RemovedUtc,
    DateTimeOffset? BaselineCompletedUtc,
    long? BaselineItemCount,
    DateTimeOffset? LastSuccessfulScanUtc,
    DateTimeOffset? LastEventUtc,
    int LastScanSkippedCount,
    string? LastScanWarning)
{
    public string PathKey => WindowsPath.ToKey(DisplayPath);

    public string Name => WindowsPath.GetName(DisplayPath);

    public bool HasBaseline => BaselineCompletedUtc is not null;

    public bool IsRemoved => RemovedUtc is not null;
}

public enum RemovalMode
{
    KeepHistory,
    DeleteHistory,
}

public enum WatchedFolderChangeKind
{
    Added,
    Removed,
    Paused,
    Resumed,
    ExclusionsChanged,
}

/// <param name="RootId">Null only for <see cref="WatchedFolderChangeKind.ExclusionsChanged"/> on the global list.</param>
public sealed record WatchedFolderChange(WatchedFolderChangeKind Kind, long? RootId, RemovalMode? Removal = null);
