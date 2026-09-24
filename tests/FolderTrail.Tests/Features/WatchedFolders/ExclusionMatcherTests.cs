using FolderTrail.Core.Features.WatchedFolders;

namespace FolderTrail.Tests.Features.WatchedFolders;

public sealed class ExclusionMatcherTests
{
    private static readonly ExclusionMatcher Defaults = new(ExclusionPattern.Suggested);

    [Theory]
    [InlineData(@".git", true)]
    [InlineData(@".git\objects\ab\cdef", true)]
    [InlineData(@"src\.git\HEAD", true)]
    [InlineData(@"project\bin\Debug\app.dll", true)]
    [InlineData(@"project\obj\project.assets.json", true)]
    [InlineData(@"web\node_modules\left-pad\index.js", true)]
    [InlineData(@"docs\draft.tmp", true)]
    [InlineData(@"docs\~$report.docx", true)]
    [InlineData(@"DOCS\DRAFT.TMP", true)]
    [InlineData(@"Photos\Thumbs.db", true)]
    [InlineData(@"docs\report.docx", false)]
    [InlineData(@"binary\file.txt", false)]
    [InlineData(@"robin\notes.md", false)]
    [InlineData(@"objects.txt", false)]
    [InlineData(@"docs\template.tmpl", false)]
    public void Suggested_defaults_exclude_common_noise_only(string relativePath, bool excluded) =>
        Assert.Equal(excluded, Defaults.IsExcluded(relativePath));

    [Fact]
    public void The_watched_folder_itself_is_never_excluded() => Assert.False(new ExclusionMatcher(["*.txt", "bin"]).IsExcluded(string.Empty));

    [Theory]
    [InlineData("build/output", @"build\output\app.exe", true)]
    [InlineData("build/output", @"build\output", true)]
    [InlineData("build/output", @"src\build\output\app.exe", false)]
    [InlineData("build/output", @"build\outputs\app.exe", false)]
    [InlineData("docs/**/*.pdf", @"docs\a\b\c.pdf", true)]
    [InlineData("docs/**/*.pdf", @"docs\c.pdf", true)]
    [InlineData("docs/**/*.pdf", @"other\c.pdf", false)]
    [InlineData("**/cache", @"a\b\cache\x.bin", true)]
    [InlineData("**/cache", @"cache", true)]
    [InlineData("/Logs/", @"logs\today.log", true)]
    public void Path_patterns_are_anchored_to_the_watched_folder(string pattern, string relativePath, bool excluded) =>
        Assert.Equal(excluded, new ExclusionMatcher([pattern]).IsExcluded(relativePath));

    [Fact]
    public void Name_patterns_match_unicode_names_case_insensitively() =>
        Assert.True(new ExclusionMatcher(["Брouillon*"]).IsExcluded(@"Документы\бРOUILLON-1.txt"));

    [Fact]
    public void Question_mark_matches_exactly_one_character_within_a_name()
    {
        var matcher = new ExclusionMatcher(["file?.log"]);
        Assert.True(matcher.IsExcluded("file1.log"));
        Assert.False(matcher.IsExcluded("file12.log"));
        Assert.False(matcher.IsExcluded(@"file\1.log"));
    }

    [Fact]
    public void Regex_metacharacters_in_patterns_are_literal()
    {
        var matcher = new ExclusionMatcher(["(draft)+[1].txt"]);
        Assert.True(matcher.IsExcluded("(draft)+[1].txt"));
        Assert.False(matcher.IsExcluded("draftdraft1.txt"));
    }

    [Theory]
    [InlineData("", "Enter a name or pattern.")]
    [InlineData("*", "This pattern would exclude everything.")]
    [InlineData("**", "This pattern would exclude everything.")]
    [InlineData("*.*", "This pattern would exclude everything.")]
    [InlineData(@"C:\Temp", "Patterns can't contain")]
    [InlineData("a|b", "Patterns can't contain")]
    [InlineData("../outside", "Patterns can't contain \"..\".")]
    public void Validate_explains_invalid_patterns(string pattern, string expectedStart) =>
        Assert.StartsWith(expectedStart, ExclusionPattern.Validate(pattern));

    [Theory]
    [InlineData("node_modules")]
    [InlineData("*.bak")]
    [InlineData("build/output")]
    [InlineData("docs/**/*.pdf")]
    public void Validate_accepts_reasonable_patterns(string pattern) => Assert.Null(ExclusionPattern.Validate(pattern));

    [Fact]
    public void Invalid_patterns_are_ignored_by_the_matcher_instead_of_excluding_everything()
    {
        var matcher = new ExclusionMatcher(["*", "..", "keep.me"]);
        Assert.False(matcher.IsExcluded("anything.txt"));
        Assert.True(matcher.IsExcluded("keep.me"));
        Assert.Equal(["keep.me"], matcher.Patterns);
    }

    [Fact]
    public void Absolute_overload_resolves_relative_to_root() =>
        Assert.True(Defaults.IsExcluded(@"C:\Work", @"c:\work\App\BIN\x.dll"));
}
