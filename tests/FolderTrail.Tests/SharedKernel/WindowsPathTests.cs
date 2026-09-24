using FolderTrail.Core.SharedKernel.Paths;

namespace FolderTrail.Tests.SharedKernel;

public sealed class WindowsPathTests
{
    [Theory]
    [InlineData(@"C:\Users\Ana\Documents", @"C:\Users\Ana\Documents")]
    [InlineData(@"c:\users\ana\documents\", @"C:\users\ana\documents")]
    [InlineData("C:/Users/Ana/Documents", @"C:\Users\Ana\Documents")]
    [InlineData(@"C:\Users\\Ana\.\Documents\..\Documents", @"C:\Users\Ana\Documents")]
    [InlineData(@"\\?\C:\Very\Long\Path", @"C:\Very\Long\Path")]
    [InlineData(@"\\?\UNC\server\share\folder", @"\\server\share\folder")]
    [InlineData(@"\\server\share\folder\", @"\\server\share\folder")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData("D:", @"D:\")]
    [InlineData(@"\\server\share", @"\\server\share\")]
    public void Canonicalize_normalizes_separators_case_of_drive_and_dot_segments(string input, string expected) =>
        Assert.Equal(expected, WindowsPath.Canonicalize(input));

    [Theory]
    [InlineData("relative\\path")]
    [InlineData("C:relative")]
    [InlineData(@"\\server")]
    [InlineData("   ")]
    public void Canonicalize_rejects_paths_that_are_not_absolute(string input)
    {
        Assert.Throws<ArgumentException>(() => WindowsPath.Canonicalize(input));
        Assert.False(WindowsPath.TryCanonicalize(input, out _));
    }

    [Fact]
    public void Keys_compare_case_insensitively_including_non_ascii_letters()
    {
        var a = WindowsPath.ToKey(WindowsPath.Canonicalize(@"C:\Données\Résumé.docx"));
        var b = WindowsPath.ToKey(WindowsPath.Canonicalize(@"c:\DONNÉES\résumé.DOCX"));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Keys_preserve_unicode_normalization_form_because_ntfs_treats_them_as_different_names()
    {
        var composed = WindowsPath.ToKey(@"C:\caf" + "\u00E9");
        var decomposed = WindowsPath.ToKey(@"C:\cafe" + "\u0301");
        Assert.NotEqual(composed, decomposed);
    }

    [Fact]
    public void Display_path_keeps_original_casing_and_unicode()
    {
        const string path = @"C:\Проекты\日本語\Ünïcödé.txt";
        Assert.Equal(path, WindowsPath.Canonicalize(path));
        Assert.Equal("Ünïcödé.txt", WindowsPath.GetName(path));
    }

    [Theory]
    [InlineData(@"C:\DATA", @"C:\DATA", true)]
    [InlineData(@"C:\DATA", @"C:\DATA\FILE.TXT", true)]
    [InlineData(@"C:\DATA", @"C:\DATABASE", false)]
    [InlineData(@"C:\DATA", @"C:\DAT", false)]
    [InlineData(@"C:\", @"C:\ANYTHING", true)]
    [InlineData(@"\\SERVER\SHARE\", @"\\SERVER\SHARE\A", true)]
    public void IsSameOrDescendant_respects_segment_boundaries(string ancestor, string candidate, bool expected) =>
        Assert.Equal(expected, WindowsPath.IsSameOrDescendant(ancestor, candidate));

    [Theory]
    [InlineData(@"C:\a\b.txt", @"C:\a")]
    [InlineData(@"C:\a", @"C:\")]
    [InlineData(@"\\server\share\a", @"\\server\share\")]
    [InlineData(@"\\server\share\a\b", @"\\server\share\a")]
    public void GetParent_returns_volume_roots_with_trailing_separator(string path, string expected) =>
        Assert.Equal(expected, WindowsPath.GetParent(path));

    [Fact]
    public void GetParent_of_volume_root_is_null() => Assert.Null(WindowsPath.GetParent(@"C:\"));

    [Fact]
    public void Relative_paths_and_ancestor_replacement_round_trip()
    {
        Assert.Equal(@"sub\file.txt", WindowsPath.GetRelativePath(@"C:\Root", @"c:\root\sub\file.txt"));
        Assert.Equal(string.Empty, WindowsPath.GetRelativePath(@"C:\Root", @"C:\ROOT"));
        Assert.Equal(@"file.txt", WindowsPath.GetRelativePath(@"C:\", @"C:\file.txt"));
        Assert.Equal(@"C:\Root\Renamed\file.txt", WindowsPath.ReplaceAncestor(@"C:\Root\Old\file.txt", @"C:\Root\Old", @"C:\Root\Renamed"));
        Assert.Throws<ArgumentException>(() => WindowsPath.GetRelativePath(@"C:\Root", @"C:\Other\file.txt"));
    }

    [Fact]
    public void Descendant_range_covers_children_but_not_siblings_with_common_prefix()
    {
        var start = WindowsPath.DescendantRangeStart(@"C:\A");
        var end = WindowsPath.DescendantRangeEnd(@"C:\A");
        bool InRange(string key) => string.CompareOrdinal(key, start) >= 0 && string.CompareOrdinal(key, end) < 0;

        Assert.True(InRange(@"C:\A\B"));
        Assert.True(InRange(@"C:\A\B\C.TXT"));
        Assert.False(InRange(@"C:\A"));
        Assert.False(InRange(@"C:\AB"));
        Assert.False(InRange(@"C:\A-B"));
    }

    [Theory]
    [InlineData(@"C:\a\b", @"C:\")]
    [InlineData(@"\\server\share\a\b", @"\\server\share\")]
    public void GetVolumeRoot_returns_drive_or_share(string path, string expected) =>
        Assert.Equal(expected, WindowsPath.GetVolumeRoot(path));
}
