using FolderTrail.Core.Features.Reconciliation;
using FolderTrail.Core.SharedKernel.Journal;

namespace FolderTrail.Tests.Support;

public sealed class FakeScanner : IFileSystemScanner
{
    private static readonly DateTimeOffset Recorded = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly Queue<ScanOutcome> _outcomes = new();

    public int Calls { get; private set; }

    public bool ThrowCancellation { get; init; }

    public static InventoryEntry File(string path, long length = 1) => InventoryEntry.Create(path, ItemKind.File, length, Recorded, Recorded);

    public static InventoryEntry Folder(string path) => InventoryEntry.Create(path, ItemKind.Directory, null, Recorded, Recorded);

    public void Next(params InventoryEntry[] items) => Next(items, [], 0);

    public void Next(InventoryEntry[] items, string[] unreadable, int skipped) =>
        _outcomes.Enqueue(new ScanOutcome(items.ToDictionary(i => i.PathKey, StringComparer.Ordinal), skipped, unreadable, [], false, Recorded, Recorded));

    public ScanOutcome Scan(ScanRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        if (ThrowCancellation)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return _outcomes.Count > 0 ? _outcomes.Dequeue() : new ScanOutcome(new Dictionary<string, InventoryEntry>(), 0, [], [], false, Recorded, Recorded);
    }
}
