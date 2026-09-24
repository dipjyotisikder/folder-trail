using FolderTrail.Core.Features.WatchedFolders;

namespace FolderTrail.Tests.Features.WatchedFolders;

public sealed class RootOverlapTests
{
    private static WatchedRoot Root(long id, string path, bool subfolders = true) =>
        new(id, path, subfolders, false, DateTimeOffset.UnixEpoch, null, null, null, null, null, 0, null);

    [Fact]
    public void Same_folder_with_different_casing_is_a_duplicate()
    {
        var check = RootOverlap.Analyze(@"C:\USERS\ana\docs", true, [Root(1, @"C:\Users\Ana\Docs")]);
        Assert.Equal(AddFolderOutcome.Duplicate, check.Outcome);
        Assert.False(check.CanAdd);
    }

    [Fact]
    public void Child_of_a_recursive_watch_is_rejected_and_names_the_parent()
    {
        var check = RootOverlap.Analyze(@"C:\Work\Project\src", true, [Root(1, @"C:\Work"), Root(2, @"C:\Work\Project")]);
        Assert.Equal(AddFolderOutcome.CoveredByExisting, check.Outcome);
        Assert.Equal(2, check.RelatedRoots.Single().Id);
        Assert.Contains(@"C:\Work\Project", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Child_of_a_non_recursive_watch_is_allowed() =>
        Assert.Equal(AddFolderOutcome.Ok, RootOverlap.Analyze(@"C:\Work\Project", true, [Root(1, @"C:\Work", subfolders: false)]).Outcome);

    [Fact]
    public void Recursive_parent_of_existing_watches_requires_merging_them()
    {
        var check = RootOverlap.Analyze(@"C:\Work", true, [Root(1, @"C:\Work\A"), Root(2, @"C:\Work\B\C"), Root(3, @"D:\Other")]);
        Assert.Equal(AddFolderOutcome.ContainsExisting, check.Outcome);
        Assert.True(check.CanAdd);
        Assert.True(check.RequiresMerge);
        Assert.Equal([1L, 2L], check.RelatedRoots.Select(r => r.Id).Order());
        Assert.Contains("history is kept", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_recursive_parent_does_not_overlap_its_children() =>
        Assert.Equal(AddFolderOutcome.Ok, RootOverlap.Analyze(@"C:\Work", false, [Root(1, @"C:\Work\A")]).Outcome);

    [Fact]
    public void Sibling_with_a_common_prefix_is_not_an_overlap() =>
        Assert.Equal(AddFolderOutcome.Ok, RootOverlap.Analyze(@"C:\Database", true, [Root(1, @"C:\Data")]).Outcome);
}
