using FolderTrail.Core.SharedKernel.Journal;
using FolderTrail.Core.SharedKernel.Paths;
using FolderTrail.Core.SharedKernel.Persistence;
using Microsoft.Data.Sqlite;

namespace FolderTrail.Core.Features.Reconciliation;

/// <summary>What FolderTrail last knew about one item: metadata only, no contents or hashes.</summary>
public sealed record InventoryEntry(string PathKey, string DisplayPath, ItemKind Kind, long? Length, DateTimeOffset? LastWriteUtc, DateTimeOffset RecordedUtc)
{
    public static InventoryEntry Create(string canonicalPath, ItemKind kind, long? length, DateTimeOffset? lastWriteUtc, DateTimeOffset recordedUtc) =>
        new(WindowsPath.ToKey(canonicalPath), canonicalPath, kind, kind == ItemKind.File ? length : null, lastWriteUtc, recordedUtc);
}

/// <summary>
/// The inventory is current state for watched folders, not history: retention never prunes it, because it's
/// what scans compare against. Callers own the connection and transaction.
/// </summary>
internal static class InventoryStore
{
    private const string Columns = "path_key, display_path, kind, length, last_write_utc, recorded_utc";

    public static Dictionary<string, InventoryEntry> LoadRoot(SqliteConnection connection, long rootId, SqliteTransaction? transaction = null)
    {
        using var command = connection.Command($"SELECT {Columns} FROM inventory WHERE root_id = $root;", transaction).With("$root", rootId);
        var entries = new Dictionary<string, InventoryEntry>(StringComparer.Ordinal);
        foreach (var entry in Read(command))
        {
            entries[entry.PathKey] = entry;
        }

        return entries;
    }

    public static InventoryEntry? Get(SqliteConnection connection, SqliteTransaction? transaction, long rootId, string pathKey)
    {
        using var command = connection.Command($"SELECT {Columns} FROM inventory WHERE root_id = $root AND path_key = $key;", transaction)
            .With("$root", rootId)
            .With("$key", pathKey);
        return Read(command).FirstOrDefault();
    }

    public static List<InventoryEntry> GetDescendants(SqliteConnection connection, SqliteTransaction? transaction, long rootId, string pathKey)
    {
        using var command = connection.Command(
                $"SELECT {Columns} FROM inventory WHERE root_id = $root AND path_key >= $start AND path_key < $end;",
                transaction)
            .With("$root", rootId)
            .With("$start", WindowsPath.DescendantRangeStart(pathKey))
            .With("$end", WindowsPath.DescendantRangeEnd(pathKey));
        return Read(command).ToList();
    }

    public static long Count(SqliteConnection connection, long? rootId = null)
    {
        using var command = connection.Command(
                rootId is null ? "SELECT COUNT(*) FROM inventory;" : "SELECT COUNT(*) FROM inventory WHERE root_id = $root;")
            .With("$root", rootId);
        return command.ScalarInt64();
    }

    public static void Upsert(SqliteConnection connection, SqliteTransaction transaction, long rootId, IEnumerable<InventoryEntry> entries)
    {
        using var command = connection.Command(
            """
            INSERT INTO inventory (root_id, path_key, display_path, kind, length, last_write_utc, recorded_utc)
            VALUES ($root, $key, $path, $kind, $length, $lastWrite, $recorded)
            ON CONFLICT (root_id, path_key) DO UPDATE SET
                display_path = excluded.display_path, kind = excluded.kind, length = excluded.length,
                last_write_utc = excluded.last_write_utc, recorded_utc = excluded.recorded_utc;
            """,
            transaction);
        command.Parameters.AddWithValue("$root", rootId);
        var key = command.Parameters.Add("$key", SqliteType.Text);
        var path = command.Parameters.Add("$path", SqliteType.Text);
        var kind = command.Parameters.Add("$kind", SqliteType.Integer);
        var length = command.Parameters.Add("$length", SqliteType.Integer);
        var lastWrite = command.Parameters.Add("$lastWrite", SqliteType.Integer);
        var recorded = command.Parameters.Add("$recorded", SqliteType.Integer);
        command.Prepare();
        foreach (var entry in entries)
        {
            key.Value = entry.PathKey;
            path.Value = entry.DisplayPath;
            kind.Value = (int)entry.Kind;
            length.Value = (object?)entry.Length ?? DBNull.Value;
            lastWrite.Value = (object?)UnixTime.ToMs(entry.LastWriteUtc) ?? DBNull.Value;
            recorded.Value = UnixTime.ToMs(entry.RecordedUtc);
            command.ExecuteNonQuery();
        }
    }

    public static void Delete(SqliteConnection connection, SqliteTransaction transaction, long rootId, IEnumerable<string> pathKeys)
    {
        using var command = connection.Command("DELETE FROM inventory WHERE root_id = $root AND path_key = $key;", transaction);
        command.Parameters.AddWithValue("$root", rootId);
        var key = command.Parameters.Add("$key", SqliteType.Text);
        command.Prepare();
        foreach (var pathKey in pathKeys)
        {
            key.Value = pathKey;
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Deletes an item and, for folders, everything recorded beneath it.</summary>
    public static void DeleteSubtree(SqliteConnection connection, SqliteTransaction transaction, long rootId, string pathKey)
    {
        Delete(connection, transaction, rootId, [pathKey]);
        using var command = connection.Command(
                "DELETE FROM inventory WHERE root_id = $root AND path_key >= $start AND path_key < $end;",
                transaction)
            .With("$root", rootId)
            .With("$start", WindowsPath.DescendantRangeStart(pathKey))
            .With("$end", WindowsPath.DescendantRangeEnd(pathKey));
        command.ExecuteNonQuery();
    }

    /// <summary>Re-keys an item and everything beneath it after a rename. Returns the moved entries' new form.</summary>
    public static List<InventoryEntry> MoveSubtree(SqliteConnection connection, SqliteTransaction transaction, long rootId, string oldCanonical, string newCanonical)
    {
        var oldKey = WindowsPath.ToKey(oldCanonical);
        var moved = new List<InventoryEntry>();
        var self = Get(connection, transaction, rootId, oldKey);
        if (self is not null)
        {
            moved.Add(self with { PathKey = WindowsPath.ToKey(newCanonical), DisplayPath = newCanonical });
        }

        foreach (var child in GetDescendants(connection, transaction, rootId, oldKey))
        {
            var newPath = WindowsPath.ReplaceAncestor(child.DisplayPath, oldCanonical, newCanonical);
            moved.Add(child with { PathKey = WindowsPath.ToKey(newPath), DisplayPath = newPath });
        }

        DeleteSubtree(connection, transaction, rootId, oldKey);
        Upsert(connection, transaction, rootId, moved);
        return moved;
    }

    public static void DeleteAll(SqliteConnection connection, SqliteTransaction transaction, long rootId)
    {
        using var command = connection.Command("DELETE FROM inventory WHERE root_id = $root;", transaction).With("$root", rootId);
        command.ExecuteNonQuery();
    }

    /// <summary>Removes inventory left behind by folders that are no longer watched.</summary>
    public static int DeleteForRemovedRoots(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.Command(
            "DELETE FROM inventory WHERE root_id IN (SELECT id FROM watched_roots WHERE removed_utc IS NOT NULL);",
            transaction);
        return command.ExecuteNonQuery();
    }

    private static IEnumerable<InventoryEntry> Read(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return new InventoryEntry(
                reader.GetString(0),
                reader.GetString(1),
                (ItemKind)reader.GetInt32(2),
                reader.GetNullableInt64(3),
                reader.GetNullableTime(4),
                reader.GetTime(5));
        }
    }
}
