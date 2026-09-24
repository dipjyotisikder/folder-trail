using FolderTrail.Core.SharedKernel.Journal;
using FolderTrail.Core.SharedKernel.Paths;

namespace FolderTrail.Core.Features.Timeline;

/// <param name="SearchText">Every whitespace-separated term must appear in the name or path (or old path).</param>
/// <param name="FromUtc">Inclusive lower bound on detection time.</param>
/// <param name="ToUtc">Exclusive upper bound on detection time.</param>
public sealed record TimelineFilter(
    string? SearchText = null,
    IReadOnlyCollection<long>? RootIds = null,
    IReadOnlyCollection<ActivityType>? Types = null,
    IReadOnlyCollection<ActivitySource>? Sources = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null)
{
    public static TimelineFilter All { get; } = new();

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(SearchText) && RootIds is null or { Count: 0 } && Types is null or { Count: 0 }
        && Sources is null or { Count: 0 } && FromUtc is null && ToUtc is null;
}

/// <summary>Keyset position: newest-first ordering by (detected time, id).</summary>
public sealed record TimelineCursor(DateTimeOffset DetectedUtc, long Id);

public sealed record TimelineEntry(
    long Id,
    long RootId,
    string RootPath,
    bool RootRemoved,
    DateTimeOffset DetectedUtc,
    ActivityType Type,
    ActivitySource Source,
    ItemKind Kind,
    string Path,
    string Name,
    string? OldPath,
    long? Length,
    DateTimeOffset? LastWriteUtc,
    long? PreviousLength,
    DateTimeOffset? PreviousLastWriteUtc,
    DateTimeOffset? PreviousObservedUtc,
    int MergedCount)
{
    public string? ParentFolder => WindowsPath.GetParent(Path);

    public string? OldName => OldPath is null ? null : WindowsPath.GetName(OldPath);

    public TimelineCursor Cursor => new(DetectedUtc, Id);

    /// <summary>True when the record was inferred by comparing scans, so the true change time is unknown.</summary>
    public bool IsInferred => Source != ActivitySource.Live;
}

public sealed record TimelinePage(IReadOnlyList<TimelineEntry> Items, TimelineCursor? Next);

public sealed record TimelineRootOption(long RootId, string DisplayPath, bool IsRemoved)
{
    public string Name => WindowsPath.GetName(DisplayPath);
}

public static class ActivityText
{
    public static string Verb(ActivityType type) => type switch
    {
        ActivityType.Created => "Created",
        ActivityType.Modified => "Modified",
        ActivityType.Deleted => "Deleted",
        ActivityType.Renamed => "Renamed",
        _ => type.ToString(),
    };

    public static string Source(ActivitySource source) => source switch
    {
        ActivitySource.Live => "Live",
        ActivitySource.Reconciliation => "Reconciliation",
        ActivitySource.ManualScan => "Manual scan",
        _ => source.ToString(),
    };

    public static string Kind(ItemKind kind) => kind switch
    {
        ItemKind.File => "File",
        ItemKind.Directory => "Folder",
        _ => "Unknown",
    };
}
