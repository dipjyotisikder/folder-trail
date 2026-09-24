using FolderTrail.Core.SharedKernel.Journal;
using FolderTrail.Core.SharedKernel.Paths;

namespace FolderTrail.Core.Features.Reconciliation;

/// <summary>
/// Paths the live watcher reported while a scan ran. A created, deleted or renamed item protects its whole subtree
/// (its contents moved with it); a modification protects only that exact path, because a folder's own "changed"
/// notification says nothing about the items inside it.
/// </summary>
public sealed record TouchedPaths(IReadOnlySet<string> Subtrees, IReadOnlySet<string> Exact)
{
    public static TouchedPaths None { get; } = new(new HashSet<string>(), new HashSet<string>());

    public bool IsEmpty => Subtrees.Count == 0 && Exact.Count == 0;
}

public sealed record InventoryChange(ActivityType Type, InventoryEntry? Previous, InventoryEntry? Current)
{
    public string PathKey => (Current ?? Previous)!.PathKey;
}

/// <summary>
/// Net differences between what was recorded and what a scan found. Renames are never inferred: without content
/// identity a disappearance plus an appearance is reported as exactly that. Unchanged items produce nothing.
/// </summary>
public static class InventoryDiffer
{
    /// <param name="unreadableFolderKeys">Folders the scan couldn't list; their recorded contents are left alone.</param>
    /// <param name="isProtected">Paths the live watcher touched during the scan; the live record is authoritative.</param>
    public static IReadOnlyList<InventoryChange> Compare(
        IReadOnlyDictionary<string, InventoryEntry> stored,
        IReadOnlyDictionary<string, InventoryEntry> current,
        IReadOnlyCollection<string> unreadableFolderKeys,
        Func<string, bool>? isProtected = null)
    {
        var unreadable = new HashSet<string>(unreadableFolderKeys, StringComparer.Ordinal);
        var changes = new List<InventoryChange>();

        foreach (var (key, previous) in stored)
        {
            if (isProtected?.Invoke(key) == true)
            {
                continue;
            }

            if (!current.TryGetValue(key, out var now))
            {
                // An item whose own metadata, or whose folder, couldn't be read isn't evidence of a deletion.
                if (!unreadable.Contains(key) && !HasAncestorIn(key, unreadable))
                {
                    changes.Add(new InventoryChange(ActivityType.Deleted, previous, null));
                }
            }
            else if (now.Kind != previous.Kind)
            {
                changes.Add(new InventoryChange(ActivityType.Deleted, previous, null));
                changes.Add(new InventoryChange(ActivityType.Created, null, now));
            }
            else if (now.Kind == ItemKind.File && (now.Length != previous.Length || now.LastWriteUtc != previous.LastWriteUtc))
            {
                changes.Add(new InventoryChange(ActivityType.Modified, previous, now));
            }
        }

        foreach (var (key, now) in current)
        {
            if (!stored.ContainsKey(key) && isProtected?.Invoke(key) != true)
            {
                changes.Add(new InventoryChange(ActivityType.Created, null, now));
            }
        }

        return changes
            .OrderBy(c => c.PathKey, StringComparer.Ordinal)
            .ThenBy(c => c.Type == ActivityType.Deleted ? 0 : 1)
            .ToList();
    }

    public static Func<string, bool> Protect(TouchedPaths touched)
    {
        if (touched.IsEmpty)
        {
            return static _ => false;
        }

        return key => touched.Exact.Contains(key) || touched.Subtrees.Contains(key) || HasAncestorIn(key, touched.Subtrees);
    }

    private static bool HasAncestorIn(string key, IReadOnlySet<string> folders)
    {
        if (folders.Count == 0)
        {
            return false;
        }

        for (var parent = WindowsPath.GetParent(key); parent is not null; parent = WindowsPath.GetParent(parent))
        {
            if (folders.Contains(parent))
            {
                return true;
            }
        }

        return false;
    }
}
