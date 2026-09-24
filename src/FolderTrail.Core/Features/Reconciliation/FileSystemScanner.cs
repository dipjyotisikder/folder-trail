using System.Security;
using FolderTrail.Core.Features.WatchedFolders;
using FolderTrail.Core.SharedKernel.FileSystem;
using FolderTrail.Core.SharedKernel.Journal;
using FolderTrail.Core.SharedKernel.Paths;
using FolderTrail.Core.SharedKernel.Persistence;

namespace FolderTrail.Core.Features.Reconciliation;

/// <param name="StartPath">Scan only this folder's subtree (it must be inside <paramref name="RootPath"/>).
/// Exclusions are still evaluated relative to the watched root.</param>
/// <param name="MaxItems">Stop early (setting <see cref="ScanOutcome.Truncated"/>) after this many items.</param>
public sealed record ScanRequest(
    string RootPath,
    bool IncludeSubfolders,
    ExclusionMatcher Exclusions,
    string? StartPath = null,
    int? MaxItems = null);

public sealed record ScanIssue(string Path, string Problem);

public sealed record ScanOutcome(
    IReadOnlyDictionary<string, InventoryEntry> Items,
    int SkippedCount,
    IReadOnlyList<string> UnreadableFolderKeys,
    IReadOnlyList<ScanIssue> Issues,
    bool Truncated,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc);

public sealed class RootUnavailableException(FolderProbeResult availability)
    : Exception("The watched folder isn't available: " + availability.Availability)
{
    public FolderProbeResult Availability { get; } = availability;
}

public interface IFileSystemScanner
{
    /// <exception cref="RootUnavailableException">The watched folder itself can't be listed.</exception>
    ScanOutcome Scan(ScanRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Walks a watched folder collecting metadata. One unreadable item never aborts the scan: it's counted and
/// its folder is reported as unreadable so nothing beneath it is mistaken for deleted. Reparse points
/// (junctions, symbolic links) are recorded as items but never followed.
/// </summary>
public sealed class FileSystemScanner(IFolderProbe probe, TimeProvider time) : IFileSystemScanner
{
    private const int MaxIssues = 50;

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,
        MatchType = MatchType.Simple,
    };

    public ScanOutcome Scan(ScanRequest request, CancellationToken cancellationToken)
    {
        var started = time.GetUtcNow();
        var start = request.StartPath ?? request.RootPath;
        var scanningRoot = WindowsPath.ToKey(start) == WindowsPath.ToKey(request.RootPath);
        var items = new Dictionary<string, InventoryEntry>(StringComparer.Ordinal);
        var unreadable = new List<string>();
        var issues = new List<ScanIssue>();
        var skipped = 0;
        var truncated = false;

        var pending = new Stack<(string Path, string Relative)>();
        pending.Push((start, WindowsPath.GetRelativePath(request.RootPath, start)));
        var isStartFolder = true;

        while (pending.Count > 0 && !truncated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (folder, relative) = pending.Pop();
            var isStart = isStartFolder;
            isStartFolder = false;
            try
            {
                var visited = 0;
                foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", Options))
                {
                    if ((++visited & 0xFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    var childPath = WindowsPath.Combine(folder, info.Name);
                    var childRelative = relative.Length == 0 ? info.Name : relative + WindowsPath.Separator + info.Name;
                    if (request.Exclusions.IsExcluded(childRelative))
                    {
                        continue;
                    }

                    InventoryEntry entry;
                    FileAttributes attributes;
                    try
                    {
                        attributes = info.Attributes;
                        entry = info is DirectoryInfo
                            ? InventoryEntry.Create(childPath, ItemKind.Directory, null, UnixTime.FromFileTimeUtc(info.LastWriteTimeUtc), started)
                            : InventoryEntry.Create(childPath, ItemKind.File, ((FileInfo)info).Length, UnixTime.FromFileTimeUtc(info.LastWriteTimeUtc), started);
                    }
                    catch (Exception exception) when (IsSkippable(exception))
                    {
                        skipped++;
                        unreadable.Add(WindowsPath.ToKey(childPath));
                        AddIssue(issues, childPath, exception);
                        continue;
                    }

                    items[entry.PathKey] = entry;
                    if (request.MaxItems is { } max && items.Count >= max)
                    {
                        truncated = true;
                        break;
                    }

                    if (entry.Kind == ItemKind.Directory && request.IncludeSubfolders && (attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push((childPath, childRelative));
                    }
                }
            }
            catch (Exception exception) when (IsSkippable(exception))
            {
                if (isStart && scanningRoot)
                {
                    var availability = probe.Probe(request.RootPath);
                    throw new RootUnavailableException(
                        availability.IsAvailable ? new FolderProbeResult(FolderAvailability.Error, exception.Message) : availability);
                }

                skipped++;
                unreadable.Add(WindowsPath.ToKey(folder));
                AddIssue(issues, folder, exception);
            }
        }

        return new ScanOutcome(items, skipped, unreadable, issues, truncated, started, time.GetUtcNow());
    }

    private static bool IsSkippable(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException;

    private static void AddIssue(List<ScanIssue> issues, string path, Exception exception)
    {
        if (issues.Count >= MaxIssues)
        {
            return;
        }

        var problem = exception switch
        {
            UnauthorizedAccessException => "Access denied",
            DirectoryNotFoundException => "Disappeared during the scan",
            PathTooLongException => "Path too long",
            _ => "Couldn't be read: " + exception.Message,
        };
        issues.Add(new ScanIssue(path, problem));
    }
}
