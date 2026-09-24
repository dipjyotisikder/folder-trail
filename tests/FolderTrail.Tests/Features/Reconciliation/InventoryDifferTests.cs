using FolderTrail.Core.Features.Reconciliation;
using FolderTrail.Core.SharedKernel.Journal;

namespace FolderTrail.Tests.Features.Reconciliation;

public sealed class InventoryDifferTests
{
    private static readonly DateTimeOffset Recorded = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static InventoryEntry File(string path, long length, int minute = 0) =>
        InventoryEntry.Create(path, ItemKind.File, length, Recorded.AddMinutes(minute), Recorded);

    private static InventoryEntry Folder(string path) => InventoryEntry.Create(path, ItemKind.Directory, null, Recorded, Recorded);

    private static Dictionary<string, InventoryEntry> Map(params InventoryEntry[] entries) => entries.ToDictionary(e => e.PathKey, StringComparer.Ordinal);

    [Fact]
    public void Identical_snapshots_produce_no_changes()
    {
        var snapshot = Map(File(@"C:\r\a.txt", 10), Folder(@"C:\r\sub"), File(@"C:\r\sub\b.txt", 20));
        Assert.Empty(InventoryDiffer.Compare(snapshot, Map(snapshot.Values.ToArray()), []));
    }

    [Fact]
    public void Net_differences_are_created_modified_and_deleted()
    {
        var stored = Map(File(@"C:\r\keep.txt", 1), File(@"C:\r\grow.txt", 1), File(@"C:\r\gone.txt", 1), File(@"C:\r\touched.txt", 1));
        var current = Map(File(@"C:\r\keep.txt", 1), File(@"C:\r\grow.txt", 99), File(@"C:\r\new.txt", 5), File(@"C:\r\touched.txt", 1, minute: 3));

        var changes = InventoryDiffer.Compare(stored, current, []);

        Assert.Equal(
            [(ActivityType.Deleted, @"C:\R\GONE.TXT"), (ActivityType.Modified, @"C:\R\GROW.TXT"), (ActivityType.Created, @"C:\R\NEW.TXT"), (ActivityType.Modified, @"C:\R\TOUCHED.TXT")],
            changes.Select(c => (c.Type, c.PathKey)));
    }

    [Fact]
    public void A_rename_is_reported_as_a_disappearance_and_an_appearance_never_as_a_rename()
    {
        var changes = InventoryDiffer.Compare(Map(File(@"C:\r\old.txt", 42)), Map(File(@"C:\r\new.txt", 42)), []);
        Assert.Equal([ActivityType.Created, ActivityType.Deleted], changes.Select(c => c.Type).Order());
        Assert.DoesNotContain(changes, c => c.Type == ActivityType.Renamed);
    }

    [Fact]
    public void Folder_timestamp_changes_are_not_modifications()
    {
        var stored = Map(InventoryEntry.Create(@"C:\r\sub", ItemKind.Directory, null, Recorded, Recorded));
        var current = Map(InventoryEntry.Create(@"C:\r\sub", ItemKind.Directory, null, Recorded.AddHours(1), Recorded));
        Assert.Empty(InventoryDiffer.Compare(stored, current, []));
    }

    [Fact]
    public void Kind_change_at_same_path_is_delete_then_create()
    {
        var changes = InventoryDiffer.Compare(Map(File(@"C:\r\thing", 1)), Map(Folder(@"C:\r\thing")), []);
        Assert.Equal([ActivityType.Deleted, ActivityType.Created], changes.Select(c => c.Type));
    }

    [Fact]
    public void Items_under_an_unreadable_folder_are_not_reported_deleted()
    {
        var stored = Map(Folder(@"C:\r\locked"), File(@"C:\r\locked\secret.txt", 1), File(@"C:\r\locked\deep\x.txt", 1), File(@"C:\r\gone.txt", 1));
        var current = Map(Folder(@"C:\r\locked"));

        var changes = InventoryDiffer.Compare(stored, current, [@"C:\R\LOCKED"]);

        Assert.Equal(@"C:\R\GONE.TXT", Assert.Single(changes).PathKey);
    }

    [Fact]
    public void An_item_whose_own_metadata_could_not_be_read_is_not_reported_deleted()
    {
        var stored = Map(Folder(@"C:\r\locked"), File(@"C:\r\locked\inner.txt", 1), File(@"C:\r\file.txt", 1));

        var changes = InventoryDiffer.Compare(stored, Map(File(@"C:\r\file.txt", 1)), [@"C:\R\LOCKED"]);

        Assert.Empty(changes);
    }

    [Fact]
    public void Paths_touched_by_the_live_watcher_are_left_to_the_live_record()
    {
        var stored = Map(File(@"C:\r\a.txt", 1), Folder(@"C:\r\moved"), File(@"C:\r\moved\x.txt", 1));
        var current = Map(File(@"C:\r\a.txt", 2), File(@"C:\r\b.txt", 1));
        var protect = InventoryDiffer.Protect(new TouchedPaths(new HashSet<string>([@"C:\R\MOVED"]), new HashSet<string>([@"C:\R\A.TXT"])));

        var changes = InventoryDiffer.Compare(stored, current, [], protect);

        Assert.Equal(@"C:\R\B.TXT", Assert.Single(changes).PathKey);
    }

    [Fact]
    public void A_folder_modification_protects_only_the_folder_not_its_contents()
    {
        var stored = Map(Folder(@"C:\r\busy"), File(@"C:\r\busy\missed.txt", 1));
        var current = Map(Folder(@"C:\r\busy"), File(@"C:\r\busy\missed.txt", 5), File(@"C:\r\busy\new.txt", 1));
        var protect = InventoryDiffer.Protect(new TouchedPaths(new HashSet<string>(), new HashSet<string>([@"C:\R\BUSY"])));

        var changes = InventoryDiffer.Compare(stored, current, [], protect);

        Assert.Equal([ActivityType.Modified, ActivityType.Created], changes.Select(c => c.Type));
    }
}
