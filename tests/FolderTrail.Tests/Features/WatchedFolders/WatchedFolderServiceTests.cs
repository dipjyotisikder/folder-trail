using FolderTrail.Core.Features.Timeline;
using FolderTrail.Core.Features.WatchedFolders;
using FolderTrail.Core.SharedKernel.FileSystem;
using FolderTrail.Core.SharedKernel.Journal;
using FolderTrail.Core.SharedKernel.Persistence;
using FolderTrail.Tests.Support;

namespace FolderTrail.Tests.Features.WatchedFolders;

public sealed class WatchedFolderServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [WindowsFact]
    public async Task Adding_a_folder_stores_it_and_announces_the_change()
    {
        using var folder = new TempDirectory();
        using var harness = new CoreHarness();
        var changes = new List<WatchedFolderChange>();
        harness.Folders.Changed += (_, c) => changes.Add(c);

        var root = await harness.AddRootAsync(folder.Path.ToLowerInvariant() + @"\");

        Assert.Equal(FolderTrail.Core.SharedKernel.Paths.WindowsPath.ToKey(folder.Path), root.PathKey);
        Assert.False(root.DisplayPath.EndsWith('\\'));
        Assert.False(root.HasBaseline);
        Assert.Equal(new WatchedFolderChange(WatchedFolderChangeKind.Added, root.Id), Assert.Single(changes));
        Assert.Single(await harness.Folders.GetActiveAsync(Token));
    }

    [WindowsFact]
    public async Task Duplicates_are_rejected_regardless_of_case_or_trailing_separator()
    {
        using var folder = new TempDirectory();
        using var harness = new CoreHarness();
        await harness.AddRootAsync(folder.Path);

        var again = await harness.Folders.AddAsync(folder.Path.ToUpperInvariant() + @"\", true, false, Token);

        Assert.False(again.Succeeded);
        Assert.Equal(AddFolderOutcome.Duplicate, again.Check.Outcome);
    }

    [WindowsFact]
    public async Task A_missing_folder_is_rejected_and_never_created()
    {
        using var folder = new TempDirectory();
        using var harness = new CoreHarness();
        var missing = folder.Combine("does-not-exist");

        var result = await harness.Folders.AddAsync(missing, true, false, Token);

        Assert.False(result.Succeeded);
        Assert.Equal(AddFolderOutcome.FolderMissing, result.Check.Outcome);
        Assert.False(Directory.Exists(missing));
    }

    [WindowsFact]
    public async Task A_file_path_is_not_accepted_as_a_folder()
    {
        using var folder = new TempDirectory();
        using var harness = new CoreHarness();
        var file = folder.CreateFile("note.txt");

        Assert.Equal(AddFolderOutcome.NotADirectory, (await harness.Folders.CheckAsync(file, true, Token)).Outcome);
    }

    [Fact]
    public async Task Unavailable_drives_and_denied_folders_are_explained()
    {
        using var harness = new CoreHarness(probe: new FakeProbe(FolderAvailability.DriveUnavailable));
        var check = await harness.Folders.CheckAsync(@"E:\Backups", true, Token);
        Assert.Equal(AddFolderOutcome.DriveUnavailable, check.Outcome);
        Assert.Contains(@"E:\", check.Message, StringComparison.Ordinal);

        using var denied = new CoreHarness(probe: new FakeProbe(FolderAvailability.AccessDenied));
        Assert.Equal(AddFolderOutcome.AccessDenied, (await denied.Folders.CheckAsync(@"C:\Locked", true, Token)).Outcome);
    }

    [WindowsFact]
    public async Task Adding_a_parent_requires_confirmation_then_replaces_child_watches_keeping_history()
    {
        using var folder = new TempDirectory();
        folder.CreateDirectory("child");
        using var harness = new CoreHarness();
        var child = await harness.AddRootAsync(folder.Combine("child"));
        await InsertEventAsync(harness, child.Id, folder.Combine(@"child\a.txt"));

        var unconfirmed = await harness.Folders.AddAsync(folder.Path, true, mergeContainedRoots: false, Token);
        Assert.False(unconfirmed.Succeeded);
        Assert.True(unconfirmed.Check.RequiresMerge);

        var changes = new List<WatchedFolderChange>();
        harness.Folders.Changed += (_, c) => changes.Add(c);
        var confirmed = await harness.Folders.AddAsync(folder.Path, true, mergeContainedRoots: true, Token);

        Assert.True(confirmed.Succeeded);
        Assert.Equal(child.Id, Assert.Single(confirmed.MergedRoots).Id);
        Assert.Equal([WatchedFolderChangeKind.Removed, WatchedFolderChangeKind.Added], changes.Select(c => c.Kind));
        Assert.Equal(confirmed.Root!.Id, Assert.Single(await harness.Folders.GetActiveAsync(Token)).Id);
        Assert.Single(await harness.EventsAsync(new TimelineFilter(RootIds: [child.Id])));
    }

    [WindowsFact]
    public async Task Pause_and_resume_are_announced_once()
    {
        using var folder = new TempDirectory();
        using var harness = new CoreHarness();
        var root = await harness.AddRootAsync(folder.Path);
        var changes = new List<WatchedFolderChangeKind>();
        harness.Folders.Changed += (_, c) => changes.Add(c.Kind);

        await harness.Folders.PauseAsync(root.Id, Token);
        await harness.Folders.PauseAsync(root.Id, Token);
        Assert.True((await harness.Folders.GetAsync(root.Id, Token))!.IsPaused);
        await harness.Folders.ResumeAllAsync(Token);

        Assert.Equal([WatchedFolderChangeKind.Paused, WatchedFolderChangeKind.Resumed], changes);
    }

    [WindowsFact]
    public async Task Removing_and_keeping_history_leaves_records_searchable()
    {
        using var folder = new TempDirectory();
        using var harness = new CoreHarness();
        var root = await harness.AddRootAsync(folder.Path);
        await InsertEventAsync(harness, root.Id, folder.Combine("kept.txt"));

        await harness.Folders.RemoveAsync(root.Id, RemovalMode.KeepHistory, Token);

        Assert.Empty(await harness.Folders.GetActiveAsync(Token));
        var entry = Assert.Single(await harness.EventsAsync());
        Assert.True(entry.RootRemoved);
        Assert.Contains(await harness.Timeline.GetRootOptionsAsync(Token), o => o.RootId == root.Id && o.IsRemoved);
    }

    [WindowsFact]
    public async Task Removing_and_deleting_history_removes_every_record_of_the_folder()
    {
        using var folder = new TempDirectory();
        using var other = new TempDirectory();
        using var harness = new CoreHarness();
        var root = await harness.AddRootAsync(folder.Path);
        var keep = await harness.AddRootAsync(other.Path);
        await InsertEventAsync(harness, root.Id, folder.Combine("gone.txt"));
        await InsertEventAsync(harness, keep.Id, other.Combine("stays.txt"));

        await harness.Folders.RemoveAsync(root.Id, RemovalMode.DeleteHistory, Token);

        Assert.Null(await harness.Folders.GetAsync(root.Id, Token));
        Assert.Equal(keep.Id, Assert.Single(await harness.EventsAsync()).RootId);
    }

    [Fact]
    public async Task Default_exclusions_are_seeded_once_and_user_edits_survive()
    {
        using var harness = new CoreHarness();
        await harness.Folders.EnsureDefaultExclusionsAsync(Token);
        var seeded = await harness.Folders.GetExclusionsAsync(null, Token);
        Assert.Equal(ExclusionPattern.Suggested.Count, seeded.Count);
        Assert.All(seeded, r => Assert.True(r.IsBuiltIn && r.IsEnabled));

        await harness.Folders.SetExclusionsAsync(null, [new ExclusionRuleInput("node_modules", IsEnabled: false, IsBuiltIn: true), new ExclusionRuleInput("*.log", true)], Token);
        await harness.Folders.EnsureDefaultExclusionsAsync(Token);

        var edited = await harness.Folders.GetExclusionsAsync(null, Token);
        Assert.Equal(["node_modules", "*.log"], edited.Select(r => r.Pattern));
        Assert.False(edited[0].IsEnabled);
    }

    [WindowsFact]
    public async Task Effective_exclusions_combine_enabled_global_and_folder_rules()
    {
        using var folder = new TempDirectory();
        using var harness = new CoreHarness();
        var root = await harness.AddRootAsync(folder.Path);
        await harness.Folders.SetExclusionsAsync(null, [new ExclusionRuleInput("*.tmp", true), new ExclusionRuleInput("bin", false)], Token);
        await harness.Folders.SetExclusionsAsync(root.Id, [new ExclusionRuleInput("private", true)], Token);

        var matcher = await harness.Folders.GetEffectiveMatcherAsync(root.Id, Token);

        Assert.True(matcher.IsExcluded("x.tmp"));
        Assert.True(matcher.IsExcluded(@"private\x.txt"));
        Assert.False(matcher.IsExcluded(@"bin\x.dll"));
    }

    [Fact]
    public async Task Invalid_exclusions_are_rejected_with_the_reason()
    {
        using var harness = new CoreHarness();
        var error = await Assert.ThrowsAsync<ArgumentException>(() => harness.Folders.SetExclusionsAsync(null, [new ExclusionRuleInput("*", true)], Token));
        Assert.Contains("exclude everything", error.Message, StringComparison.Ordinal);
    }

    internal static Task InsertEventAsync(CoreHarness harness, long rootId, string path, DateTimeOffset? at = null, ActivityType type = ActivityType.Created, ActivitySource source = ActivitySource.Live) =>
        harness.Database.WriteAsync(
            (connection, transaction) => ActivityJournal.Append(
                connection,
                transaction,
                [new NewActivity(rootId, at ?? harness.Time.GetUtcNow(), type, source, ItemKind.File, FolderTrail.Core.SharedKernel.Paths.WindowsPath.Canonicalize(path))]),
            Token);
}
