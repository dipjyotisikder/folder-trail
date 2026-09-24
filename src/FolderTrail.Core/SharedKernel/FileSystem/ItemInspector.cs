using System.Security;
using FolderTrail.Core.SharedKernel.Journal;
using FolderTrail.Core.SharedKernel.Persistence;

namespace FolderTrail.Core.SharedKernel.FileSystem;

public sealed record ItemInfo(ItemKind Kind, long? Length, DateTimeOffset? LastWriteUtc, bool IsReparsePoint = false);

/// <summary>Reads metadata (never contents) for a single path. Returns null when the item no longer exists.</summary>
public interface IItemInspector
{
    ItemInfo? Inspect(string canonicalPath);
}

public sealed class LocalItemInspector : IItemInspector
{
    public ItemInfo? Inspect(string canonicalPath)
    {
        try
        {
            var file = new FileInfo(canonicalPath);
            if (file.Exists)
            {
                return new ItemInfo(ItemKind.File, file.Length, UnixTime.FromFileTimeUtc(file.LastWriteTimeUtc));
            }

            var directory = new DirectoryInfo(canonicalPath);
            return directory.Exists
                ? new ItemInfo(
                    ItemKind.Directory,
                    null,
                    UnixTime.FromFileTimeUtc(directory.LastWriteTimeUtc),
                    (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }
}
