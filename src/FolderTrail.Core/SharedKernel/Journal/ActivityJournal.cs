using FolderTrail.Core.SharedKernel.Paths;
using FolderTrail.Core.SharedKernel.Persistence;
using Microsoft.Data.Sqlite;

namespace FolderTrail.Core.SharedKernel.Journal;

public enum ActivityType
{
    Created = 1,
    Modified = 2,
    Deleted = 3,
    Renamed = 4,
}

public enum ActivitySource
{
    Live = 1,
    Reconciliation = 2,
    ManualScan = 3,
}

public enum ItemKind
{
    Unknown = 0,
    File = 1,
    Directory = 2,
}

/// <summary>A change record about to be appended. Contents are never stored: only names, paths and metadata.</summary>
public sealed record NewActivity(
    long RootId,
    DateTimeOffset DetectedUtc,
    ActivityType Type,
    ActivitySource Source,
    ItemKind Kind,
    string Path,
    string? OldPath = null,
    long? Length = null,
    DateTimeOffset? LastWriteUtc = null,
    long? PreviousLength = null,
    DateTimeOffset? PreviousLastWriteUtc = null,
    DateTimeOffset? PreviousObservedUtc = null,
    int MergedCount = 1);

/// <summary>Append-only writer for activity_events, shared by the live pipeline and reconciliation.</summary>
internal static class ActivityJournal
{
    public static int Append(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyCollection<NewActivity> items)
    {
        if (items.Count == 0)
        {
            return 0;
        }

        using var command = connection.Command(
            """
            INSERT INTO activity_events (
                root_id, detected_utc, event_type, source, kind, path, path_key, name, old_path, old_path_key,
                length, last_write_utc, previous_length, previous_last_write_utc, previous_observed_utc, merged_count)
            VALUES (
                $root, $detected, $type, $source, $kind, $path, $pathKey, $name, $oldPath, $oldPathKey,
                $length, $lastWrite, $prevLength, $prevLastWrite, $prevObserved, $merged);
            """,
            transaction);

        var root = command.Parameters.Add("$root", SqliteType.Integer);
        var detected = command.Parameters.Add("$detected", SqliteType.Integer);
        var type = command.Parameters.Add("$type", SqliteType.Integer);
        var source = command.Parameters.Add("$source", SqliteType.Integer);
        var kind = command.Parameters.Add("$kind", SqliteType.Integer);
        var path = command.Parameters.Add("$path", SqliteType.Text);
        var pathKey = command.Parameters.Add("$pathKey", SqliteType.Text);
        var name = command.Parameters.Add("$name", SqliteType.Text);
        var oldPath = command.Parameters.Add("$oldPath", SqliteType.Text);
        var oldPathKey = command.Parameters.Add("$oldPathKey", SqliteType.Text);
        var length = command.Parameters.Add("$length", SqliteType.Integer);
        var lastWrite = command.Parameters.Add("$lastWrite", SqliteType.Integer);
        var prevLength = command.Parameters.Add("$prevLength", SqliteType.Integer);
        var prevLastWrite = command.Parameters.Add("$prevLastWrite", SqliteType.Integer);
        var prevObserved = command.Parameters.Add("$prevObserved", SqliteType.Integer);
        var merged = command.Parameters.Add("$merged", SqliteType.Integer);
        command.Prepare();

        foreach (var item in items)
        {
            root.Value = item.RootId;
            detected.Value = UnixTime.ToMs(item.DetectedUtc);
            type.Value = (int)item.Type;
            source.Value = (int)item.Source;
            kind.Value = (int)item.Kind;
            path.Value = item.Path;
            pathKey.Value = WindowsPath.ToKey(item.Path);
            name.Value = WindowsPath.GetName(item.Path);
            oldPath.Value = (object?)item.OldPath ?? DBNull.Value;
            oldPathKey.Value = item.OldPath is null ? DBNull.Value : WindowsPath.ToKey(item.OldPath);
            length.Value = (object?)item.Length ?? DBNull.Value;
            lastWrite.Value = (object?)UnixTime.ToMs(item.LastWriteUtc) ?? DBNull.Value;
            prevLength.Value = (object?)item.PreviousLength ?? DBNull.Value;
            prevLastWrite.Value = (object?)UnixTime.ToMs(item.PreviousLastWriteUtc) ?? DBNull.Value;
            prevObserved.Value = (object?)UnixTime.ToMs(item.PreviousObservedUtc) ?? DBNull.Value;
            merged.Value = item.MergedCount;
            command.ExecuteNonQuery();
        }

        return items.Count;
    }
}
