using System.Text;
using System.Text.RegularExpressions;
using FolderTrail.Core.SharedKernel.Paths;

namespace FolderTrail.Core.Features.WatchedFolders;

/// <param name="RootId">Null for a global rule that applies to every watched folder.</param>
public sealed record ExclusionRule(long Id, long? RootId, string Pattern, bool IsEnabled, bool IsBuiltIn);

public sealed record ExclusionRuleInput(string Pattern, bool IsEnabled, bool IsBuiltIn = false);

/// <summary>
/// Pattern syntax: a pattern without a separator matches any file or folder name at any depth (<c>node_modules</c>,
/// <c>*.tmp</c>). A pattern with a separator matches a path relative to the watched folder (<c>build/output</c>,
/// <c>docs/**/*.pdf</c>). <c>*</c> and <c>?</c> stay within one name; <c>**</c> spans folders. Matching is
/// case-insensitive, and excluding a folder excludes everything inside it.
/// </summary>
public static class ExclusionPattern
{
    public static IReadOnlyList<string> Suggested { get; } =
    [
        ".git", ".svn", ".hg", ".vs", ".idea", "bin", "obj", "node_modules", "__pycache__",
        "*.tmp", "*.temp", "~$*", "*.swp", "*.crdownload", "*.part", "Thumbs.db", ".DS_Store",
    ];

    private static readonly char[] ForbiddenCharacters = ['<', '>', ':', '"', '|'];

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim().Replace('/', WindowsPath.Separator);
        while (value.StartsWith(@".\", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        value = value.Trim(WindowsPath.Separator);
        while (value.Contains(@"\\", StringComparison.Ordinal))
        {
            value = value.Replace(@"\\", @"\", StringComparison.Ordinal);
        }

        return value.Length == 0 ? null : value;
    }

    /// <returns>A user-facing error, or null when the pattern is valid.</returns>
    public static string? Validate(string? raw)
    {
        var value = Normalize(raw);
        if (value is null)
        {
            return "Enter a name or pattern.";
        }

        if (value.Length > 260)
        {
            return "The pattern is too long.";
        }

        if (value.IndexOfAny(ForbiddenCharacters) >= 0 || value.Any(char.IsControl))
        {
            return "Patterns can't contain < > : \" | characters or full paths such as C:\\Folder.";
        }

        var segments = value.Split(WindowsPath.Separator);
        if (segments.Any(s => s == ".."))
        {
            return "Patterns can't contain \"..\".";
        }

        if (segments.All(s => s.All(c => c is '*' or '?' or '.')))
        {
            return "This pattern would exclude everything.";
        }

        return null;
    }

    internal static string ToRegex(string normalizedPattern)
    {
        var builder = new StringBuilder();
        var i = 0;
        while (i < normalizedPattern.Length)
        {
            var c = normalizedPattern[i];
            if (c == '*' && i + 1 < normalizedPattern.Length && normalizedPattern[i + 1] == '*')
            {
                var followedBySeparator = i + 2 < normalizedPattern.Length && normalizedPattern[i + 2] == WindowsPath.Separator;
                builder.Append(followedBySeparator ? @"(?:.*\\)?" : ".*");
                i += followedBySeparator ? 3 : 2;
                continue;
            }

            builder.Append(c switch
            {
                '*' => @"[^\\]*",
                '?' => @"[^\\]",
                _ => Regex.Escape(c.ToString()),
            });
            i++;
        }

        return builder.ToString();
    }
}

public sealed class ExclusionMatcher
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private readonly Regex? _names;
    private readonly Regex? _paths;

    public ExclusionMatcher(IEnumerable<string> patterns)
    {
        var names = new List<string>();
        var paths = new List<string>();
        var kept = new List<string>();
        foreach (var raw in patterns)
        {
            if (ExclusionPattern.Validate(raw) is not null)
            {
                continue;
            }

            var normalized = ExclusionPattern.Normalize(raw)!;
            kept.Add(normalized);
            (normalized.Contains(WindowsPath.Separator) ? paths : names).Add(ExclusionPattern.ToRegex(normalized));
        }

        Patterns = kept;
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        _names = names.Count == 0 ? null : new Regex("^(?:" + string.Join('|', names) + ")$", options, MatchTimeout);
        _paths = paths.Count == 0 ? null : new Regex("^(?:" + string.Join('|', paths) + @")(?:\\.*)?$", options, MatchTimeout);
    }

    public static ExclusionMatcher None { get; } = new([]);

    public IReadOnlyList<string> Patterns { get; }

    /// <param name="relativePath">Path relative to the watched folder, using backslashes. Empty means the folder itself.</param>
    public bool IsExcluded(string relativePath)
    {
        if (relativePath.Length == 0)
        {
            return false;
        }

        if (_names is not null)
        {
            foreach (var segment in relativePath.Split(WindowsPath.Separator))
            {
                if (_names.IsMatch(segment))
                {
                    return true;
                }
            }
        }

        return _paths is not null && _paths.IsMatch(relativePath);
    }

    public bool IsExcluded(string rootCanonical, string canonicalPath) =>
        IsExcluded(WindowsPath.GetRelativePath(rootCanonical, canonicalPath));
}
