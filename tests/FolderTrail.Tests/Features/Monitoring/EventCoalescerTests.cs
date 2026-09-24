using FolderTrail.Core.Features.Monitoring;
using FolderTrail.Core.SharedKernel.Journal;
using FolderTrail.Core.SharedKernel.Paths;

namespace FolderTrail.Tests.Features.Monitoring;

public sealed class EventCoalescerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private static ObservedChange Change(ActivityType type, string path, double seconds, string? oldPath = null, long root = 1) =>
        new(root, type, path, WindowsPath.ToKey(path), oldPath, oldPath is null ? null : WindowsPath.ToKey(oldPath), true, oldPath is not null,
            T0.AddSeconds(seconds), T0.AddSeconds(seconds));

    [Fact]
    public void Repeated_modifications_within_the_window_become_one_record_dated_by_the_last()
    {
        var coalescer = new EventCoalescer(Window);
        var output = new List<ObservedChange>();

        coalescer.Add(Change(ActivityType.Modified, @"C:\w\a.txt", 0), output);
        coalescer.Add(Change(ActivityType.Modified, @"C:\w\a.txt", 0.2), output);
        coalescer.Add(Change(ActivityType.Modified, @"c:\W\A.TXT", 0.5), output);
        Assert.Empty(output);

        coalescer.FlushDue(T0.AddSeconds(1.0), output);
        Assert.Empty(output);

        coalescer.FlushDue(T0.AddSeconds(1.6), output);
        var merged = Assert.Single(output);
        Assert.Equal(3, merged.MergedCount);
        Assert.Equal(T0.AddSeconds(0.5), merged.DetectedUtc);
    }

    [Fact]
    public void Modifications_separated_by_a_quiet_window_stay_separate()
    {
        var coalescer = new EventCoalescer(Window);
        var output = new List<ObservedChange>();

        coalescer.Add(Change(ActivityType.Modified, @"C:\w\a.txt", 0), output);
        coalescer.FlushDue(T0.AddSeconds(2), output);
        coalescer.Add(Change(ActivityType.Modified, @"C:\w\a.txt", 5), output);
        coalescer.FlushDue(T0.AddSeconds(7), output);

        Assert.Equal(2, output.Count);
    }

    [Fact]
    public void Distinct_event_types_are_never_merged_and_keep_their_order()
    {
        var coalescer = new EventCoalescer(Window);
        var output = new List<ObservedChange>();

        coalescer.Add(Change(ActivityType.Created, @"C:\w\a.txt", 0), output);
        coalescer.Add(Change(ActivityType.Modified, @"C:\w\a.txt", 0.1), output);
        coalescer.Add(Change(ActivityType.Deleted, @"C:\w\a.txt", 0.2), output);
        coalescer.Add(Change(ActivityType.Created, @"C:\w\a.txt", 0.3), output);
        coalescer.FlushAll(output);

        Assert.Equal([ActivityType.Created, ActivityType.Modified, ActivityType.Deleted, ActivityType.Created], output.Select(o => o.Type));
    }

    [Fact]
    public void Duplicate_identical_notifications_are_dropped()
    {
        var coalescer = new EventCoalescer(Window);
        var output = new List<ObservedChange>();

        coalescer.Add(Change(ActivityType.Created, @"C:\w\a.txt", 0), output);
        coalescer.Add(Change(ActivityType.Created, @"C:\w\a.txt", 0.01), output);
        coalescer.Add(Change(ActivityType.Deleted, @"C:\w\b.txt", 0.02), output);
        coalescer.Add(Change(ActivityType.Deleted, @"C:\w\b.txt", 0.03), output);

        Assert.Equal(2, output.Count);
    }

    [Fact]
    public void A_rename_releases_the_pending_modification_of_its_old_path_first()
    {
        var coalescer = new EventCoalescer(Window);
        var output = new List<ObservedChange>();

        coalescer.Add(Change(ActivityType.Modified, @"C:\w\draft.txt", 0), output);
        coalescer.Add(Change(ActivityType.Renamed, @"C:\w\final.txt", 0.1, oldPath: @"C:\w\draft.txt"), output);

        Assert.Equal([ActivityType.Modified, ActivityType.Renamed], output.Select(o => o.Type));
        Assert.Equal(@"C:\w\draft.txt", output[1].OldPath);
    }

    [Fact]
    public void Two_renames_of_different_paths_are_both_kept()
    {
        var coalescer = new EventCoalescer(Window);
        var output = new List<ObservedChange>();

        // Typical safe save: original moved aside, temp file moved into place.
        coalescer.Add(Change(ActivityType.Renamed, @"C:\w\doc.tmp1", 0, oldPath: @"C:\w\doc.docx"), output);
        coalescer.Add(Change(ActivityType.Renamed, @"C:\w\doc.docx", 0.05, oldPath: @"C:\w\doc.tmp2"), output);

        Assert.Equal(2, output.Count);
    }

    [Fact]
    public void A_file_written_continuously_is_still_reported_periodically()
    {
        var coalescer = new EventCoalescer(Window);
        var output = new List<ObservedChange>();

        for (var i = 0; i <= 120; i++)
        {
            coalescer.Add(Change(ActivityType.Modified, @"C:\w\log.txt", i * 0.1), output);
        }

        Assert.NotEmpty(output);
        Assert.All(output, o => Assert.True(o.LastObservedUtc - o.FirstObservedUtc >= coalescer.MaxHold));
    }

    [Fact]
    public void Paths_in_different_roots_are_coalesced_independently()
    {
        var coalescer = new EventCoalescer(Window);
        var output = new List<ObservedChange>();

        coalescer.Add(Change(ActivityType.Modified, @"C:\w\a.txt", 0, root: 1), output);
        coalescer.Add(Change(ActivityType.Modified, @"C:\w\a.txt", 0.1, root: 2), output);
        coalescer.FlushAll(output);

        Assert.Equal(2, output.Count);
    }

    [Fact]
    public void Dropping_a_root_discards_only_its_pending_changes()
    {
        var coalescer = new EventCoalescer(Window);
        var output = new List<ObservedChange>();

        coalescer.Add(Change(ActivityType.Modified, @"C:\w\a.txt", 0, root: 1), output);
        coalescer.Add(Change(ActivityType.Modified, @"C:\w\a.txt", 0, root: 2), output);
        coalescer.DropRoot(1);
        coalescer.FlushAll(output);

        Assert.Equal(2, Assert.Single(output).RootId);
    }
}
