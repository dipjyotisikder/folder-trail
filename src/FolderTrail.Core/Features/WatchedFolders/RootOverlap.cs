using FolderTrail.Core.SharedKernel.Paths;

namespace FolderTrail.Core.Features.WatchedFolders;

public enum AddFolderOutcome
{
    Ok,
    Duplicate,
    CoveredByExisting,
    ContainsExisting,
    FolderMissing,
    DriveUnavailable,
    AccessDenied,
    NotADirectory,
    InvalidPath,
    Error,
}

public sealed record AddFolderCheck(
    AddFolderOutcome Outcome,
    string? CanonicalPath,
    string Message,
    IReadOnlyList<WatchedRoot> RelatedRoots)
{
    public bool CanAdd => Outcome is AddFolderOutcome.Ok or AddFolderOutcome.ContainsExisting;

    public bool RequiresMerge => Outcome == AddFolderOutcome.ContainsExisting;
}

/// <summary>
/// Overlapping watches would record the same change twice, so FolderTrail keeps a single parent watch:
/// a folder already covered by a recursive parent is rejected, and a new recursive parent replaces the
/// watches it contains (their history is kept).
/// </summary>
public static class RootOverlap
{
    public static AddFolderCheck Analyze(string canonicalPath, bool includeSubfolders, IReadOnlyCollection<WatchedRoot> activeRoots)
    {
        var key = WindowsPath.ToKey(canonicalPath);

        var duplicate = activeRoots.FirstOrDefault(r => r.PathKey == key);
        if (duplicate is not null)
        {
            return new AddFolderCheck(
                AddFolderOutcome.Duplicate,
                canonicalPath,
                "This folder is already being watched.",
                [duplicate]);
        }

        var parent = activeRoots
            .Where(r => r.IncludeSubfolders && WindowsPath.IsDescendant(r.PathKey, key))
            .OrderByDescending(r => r.PathKey.Length)
            .FirstOrDefault();
        if (parent is not null)
        {
            return new AddFolderCheck(
                AddFolderOutcome.CoveredByExisting,
                canonicalPath,
                $"This folder is inside \"{parent.DisplayPath}\", which is already watched with its subfolders, so its activity is already recorded there.",
                [parent]);
        }

        if (includeSubfolders)
        {
            var children = activeRoots.Where(r => WindowsPath.IsDescendant(key, r.PathKey)).OrderBy(r => r.PathKey, StringComparer.Ordinal).ToList();
            if (children.Count > 0)
            {
                var what = children.Count == 1 ? "1 folder you already watch" : $"{children.Count} folders you already watch";
                return new AddFolderCheck(
                    AddFolderOutcome.ContainsExisting,
                    canonicalPath,
                    $"This folder contains {what}. To avoid recording the same activity twice, FolderTrail will watch this folder instead and stop the separate watches. Their history is kept.",
                    children);
            }
        }

        return new AddFolderCheck(AddFolderOutcome.Ok, canonicalPath, "Ready to watch this folder.", []);
    }
}
