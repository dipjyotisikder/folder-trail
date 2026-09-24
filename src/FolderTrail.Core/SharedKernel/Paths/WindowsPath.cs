namespace FolderTrail.Core.SharedKernel.Paths;

/// <summary>
/// Windows path rules implemented as pure string logic, so behavior is identical on every OS the tests run on.
/// Canonical paths keep the original casing for display; keys are upper-cased for case-insensitive comparison.
/// Unicode normalization forms are intentionally preserved: NTFS treats differently composed names as distinct files.
/// </summary>
public static class WindowsPath
{
    public const char Separator = '\\';

    public static string Canonicalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0 || string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("The path is empty.", nameof(path));
        }

        var p = path.Replace('/', Separator);
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            p = @"\\" + p[8..];
        }
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            p = p[4..];
        }

        string prefix;
        string[] segments;
        if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = p[2..].Split(Separator, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                throw new ArgumentException("A network path must include a server and a share name.", nameof(path));
            }

            prefix = @"\\" + parts[0] + Separator + parts[1];
            segments = parts[2..];
        }
        else if (p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':')
        {
            if (p.Length > 2 && p[2] != Separator)
            {
                throw new ArgumentException("Drive-relative paths such as C:folder are not supported.", nameof(path));
            }

            prefix = char.ToUpperInvariant(p[0]) + ":";
            segments = p[2..].Split(Separator, StringSplitOptions.RemoveEmptyEntries);
        }
        else
        {
            throw new ArgumentException(@"The path must be absolute, for example C:\Folder or \\server\share.", nameof(path));
        }

        var stack = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                continue;
            }

            stack.Add(segment);
        }

        return stack.Count == 0 ? prefix + Separator : prefix + Separator + string.Join(Separator, stack);
    }

    public static bool TryCanonicalize(string? path, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            canonical = Canonicalize(path);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static string ToKey(string canonicalPath) => canonicalPath.ToUpperInvariant();

    /// <summary>Only volume roots such as <c>C:\</c> or <c>\\server\share\</c> end with a separator.</summary>
    public static bool IsVolumeRoot(string canonicalPath) => canonicalPath.EndsWith(Separator);

    public static bool IsSameOrDescendant(string ancestorKey, string candidateKey)
    {
        if (!candidateKey.StartsWith(ancestorKey, StringComparison.Ordinal))
        {
            return false;
        }

        if (candidateKey.Length == ancestorKey.Length)
        {
            return true;
        }

        return ancestorKey.EndsWith(Separator) || candidateKey[ancestorKey.Length] == Separator;
    }

    public static bool IsDescendant(string ancestorKey, string candidateKey) =>
        candidateKey.Length != ancestorKey.Length && IsSameOrDescendant(ancestorKey, candidateKey);

    /// <summary>Returns the path relative to <paramref name="rootCanonical"/>, or an empty string for the root itself.</summary>
    public static string GetRelativePath(string rootCanonical, string canonicalPath)
    {
        var rootKey = ToKey(rootCanonical);
        var key = ToKey(canonicalPath);
        if (!IsSameOrDescendant(rootKey, key))
        {
            throw new ArgumentException("The path is not inside the watched folder.", nameof(canonicalPath));
        }

        if (key.Length == rootKey.Length)
        {
            return string.Empty;
        }

        var start = rootKey.EndsWith(Separator) ? rootKey.Length : rootKey.Length + 1;
        return canonicalPath[start..];
    }

    public static string GetName(string canonicalPath)
    {
        if (IsVolumeRoot(canonicalPath))
        {
            return canonicalPath;
        }

        var index = canonicalPath.LastIndexOf(Separator);
        return index < 0 ? canonicalPath : canonicalPath[(index + 1)..];
    }

    public static string? GetParent(string canonicalPath)
    {
        if (IsVolumeRoot(canonicalPath))
        {
            return null;
        }

        var index = canonicalPath.LastIndexOf(Separator);
        if (index < 0)
        {
            return null;
        }

        var parent = canonicalPath[..index];
        return IsDriveOnly(parent) || IsUncShareOnly(parent) ? parent + Separator : parent;
    }

    public static string Combine(string canonicalDirectory, string relativePath)
    {
        if (relativePath.Length == 0)
        {
            return canonicalDirectory;
        }

        return IsVolumeRoot(canonicalDirectory)
            ? canonicalDirectory + relativePath
            : canonicalDirectory + Separator + relativePath;
    }

    /// <summary>Rewrites a path under <paramref name="oldAncestor"/> so it sits under <paramref name="newAncestor"/>.</summary>
    public static string ReplaceAncestor(string canonicalPath, string oldAncestor, string newAncestor) =>
        Combine(newAncestor, GetRelativePath(oldAncestor, canonicalPath));

    /// <summary>Returns <c>C:\</c> or <c>\\server\share\</c> for the given canonical path.</summary>
    public static string GetVolumeRoot(string canonicalPath)
    {
        if (canonicalPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var afterServer = canonicalPath.IndexOf(Separator, 2);
            var afterShare = afterServer < 0 ? -1 : canonicalPath.IndexOf(Separator, afterServer + 1);
            return afterShare < 0 ? canonicalPath.TrimEnd(Separator) + Separator : canonicalPath[..(afterShare + 1)];
        }

        return canonicalPath[..2] + Separator;
    }

    /// <summary>Smallest key strictly greater than every descendant key of <paramref name="key"/> (']' follows '\').</summary>
    public static string DescendantRangeEnd(string key) =>
        (key.EndsWith(Separator) ? key[..^1] : key) + (char)(Separator + 1);

    public static string DescendantRangeStart(string key) => key.EndsWith(Separator) ? key : key + Separator;

    private static bool IsDriveOnly(string value) => value.Length == 2 && value[1] == ':';

    private static bool IsUncShareOnly(string value) =>
        value.StartsWith(@"\\", StringComparison.Ordinal) && value.AsSpan(2).Count(Separator) == 1;
}
