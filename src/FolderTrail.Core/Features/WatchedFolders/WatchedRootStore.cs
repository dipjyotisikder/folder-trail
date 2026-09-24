using FolderTrail.Core.SharedKernel.Paths;
using FolderTrail.Core.SharedKernel.Persistence;
using Microsoft.Data.Sqlite;

namespace FolderTrail.Core.Features.WatchedFolders;

/// <summary>SQL for the watched_roots and exclusions tables. Callers own the connection and transaction.</summary>
internal static class WatchedRootStore
{
    private const string Columns =
        "id, display_path, include_subfolders, is_paused, added_utc, removed_utc, baseline_completed_utc, baseline_item_count, " +
        "last_successful_scan_utc, last_event_utc, last_scan_skipped_count, last_scan_warning";

    public static List<WatchedRoot> ListActive(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.Command($"SELECT {Columns} FROM watched_roots WHERE removed_utc IS NULL ORDER BY display_path COLLATE NOCASE;", transaction);
        return ReadAll(command);
    }

    public static List<WatchedRoot> ListAll(SqliteConnection connection)
    {
        using var command = connection.Command($"SELECT {Columns} FROM watched_roots ORDER BY removed_utc IS NOT NULL, display_path COLLATE NOCASE;");
        return ReadAll(command);
    }

    public static WatchedRoot? Get(SqliteConnection connection, long id, SqliteTransaction? transaction = null)
    {
        using var command = connection.Command($"SELECT {Columns} FROM watched_roots WHERE id = $id;", transaction).With("$id", id);
        return ReadAll(command).FirstOrDefault();
    }

    public static HashSet<long> ActiveIds(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.Command("SELECT id FROM watched_roots WHERE removed_utc IS NULL;", transaction);
        using var reader = command.ExecuteReader();
        var ids = new HashSet<long>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    public static long Insert(SqliteConnection connection, SqliteTransaction transaction, string canonicalPath, bool includeSubfolders, DateTimeOffset addedUtc)
    {
        using var command = connection.Command(
                "INSERT INTO watched_roots (display_path, path_key, include_subfolders, added_utc) VALUES ($path, $key, $sub, $added) RETURNING id;",
                transaction)
            .With("$path", canonicalPath)
            .With("$key", WindowsPath.ToKey(canonicalPath))
            .With("$sub", includeSubfolders ? 1 : 0)
            .With("$added", UnixTime.ToMs(addedUtc));
        return command.ScalarInt64();
    }

    public static int SetPaused(SqliteConnection connection, SqliteTransaction transaction, long? id, bool paused)
    {
        using var command = connection.Command(
                id is null
                    ? "UPDATE watched_roots SET is_paused = $paused WHERE removed_utc IS NULL AND is_paused <> $paused;"
                    : "UPDATE watched_roots SET is_paused = $paused WHERE id = $id AND removed_utc IS NULL AND is_paused <> $paused;",
                transaction)
            .With("$paused", paused ? 1 : 0)
            .With("$id", id);
        return command.ExecuteNonQuery();
    }

    public static void MarkRemoved(SqliteConnection connection, SqliteTransaction transaction, long id, DateTimeOffset removedUtc)
    {
        using var command = connection.Command(
                "UPDATE watched_roots SET removed_utc = $removed, is_paused = 0 WHERE id = $id AND removed_utc IS NULL;",
                transaction)
            .With("$removed", UnixTime.ToMs(removedUtc))
            .With("$id", id);
        command.ExecuteNonQuery();
    }

    public static void Delete(SqliteConnection connection, SqliteTransaction transaction, long id)
    {
        using var command = connection.Command("DELETE FROM watched_roots WHERE id = $id;", transaction).With("$id", id);
        command.ExecuteNonQuery();
    }

    public static void RecordBaseline(SqliteConnection connection, SqliteTransaction transaction, long id, DateTimeOffset completedUtc, long itemCount)
    {
        using var command = connection.Command(
                "UPDATE watched_roots SET baseline_completed_utc = $done, baseline_item_count = $count WHERE id = $id;",
                transaction)
            .With("$done", UnixTime.ToMs(completedUtc))
            .With("$count", itemCount)
            .With("$id", id);
        command.ExecuteNonQuery();
    }

    public static void RecordScan(SqliteConnection connection, SqliteTransaction transaction, long id, DateTimeOffset completedUtc, int skipped, string? warning)
    {
        using var command = connection.Command(
                "UPDATE watched_roots SET last_successful_scan_utc = $done, last_scan_skipped_count = $skipped, last_scan_warning = $warning WHERE id = $id;",
                transaction)
            .With("$done", UnixTime.ToMs(completedUtc))
            .With("$skipped", skipped)
            .With("$warning", warning)
            .With("$id", id);
        command.ExecuteNonQuery();
    }

    public static void RecordLastEvent(SqliteConnection connection, SqliteTransaction transaction, long id, DateTimeOffset eventUtc)
    {
        using var command = connection.Command(
                "UPDATE watched_roots SET last_event_utc = MAX(COALESCE(last_event_utc, 0), $utc) WHERE id = $id;",
                transaction)
            .With("$utc", UnixTime.ToMs(eventUtc))
            .With("$id", id);
        command.ExecuteNonQuery();
    }

    public static List<ExclusionRule> ListExclusions(SqliteConnection connection, long? rootId, SqliteTransaction? transaction = null)
    {
        using var command = connection.Command(
                rootId is null
                    ? "SELECT id, root_id, pattern, is_enabled, is_builtin FROM exclusions WHERE root_id IS NULL ORDER BY sort_order, id;"
                    : "SELECT id, root_id, pattern, is_enabled, is_builtin FROM exclusions WHERE root_id = $root ORDER BY sort_order, id;",
                transaction)
            .With("$root", rootId);
        using var reader = command.ExecuteReader();
        var rules = new List<ExclusionRule>();
        while (reader.Read())
        {
            rules.Add(new ExclusionRule(reader.GetInt64(0), reader.GetNullableInt64(1), reader.GetString(2), reader.GetBool(3), reader.GetBool(4)));
        }

        return rules;
    }

    public static void ReplaceExclusions(SqliteConnection connection, SqliteTransaction transaction, long? rootId, IReadOnlyList<ExclusionRuleInput> rules)
    {
        using (var delete = connection.Command(
                   rootId is null ? "DELETE FROM exclusions WHERE root_id IS NULL;" : "DELETE FROM exclusions WHERE root_id = $root;",
                   transaction))
        {
            delete.With("$root", rootId).ExecuteNonQuery();
        }

        using var insert = connection.Command(
            "INSERT INTO exclusions (root_id, pattern, is_enabled, is_builtin, sort_order) VALUES ($root, $pattern, $enabled, $builtin, $order);",
            transaction);
        insert.Parameters.AddWithValue("$root", (object?)rootId ?? DBNull.Value);
        var pattern = insert.Parameters.Add("$pattern", SqliteType.Text);
        var enabled = insert.Parameters.Add("$enabled", SqliteType.Integer);
        var builtin = insert.Parameters.Add("$builtin", SqliteType.Integer);
        var order = insert.Parameters.Add("$order", SqliteType.Integer);
        for (var i = 0; i < rules.Count; i++)
        {
            pattern.Value = ExclusionPattern.Normalize(rules[i].Pattern);
            enabled.Value = rules[i].IsEnabled ? 1 : 0;
            builtin.Value = rules[i].IsBuiltIn ? 1 : 0;
            order.Value = i;
            insert.ExecuteNonQuery();
        }
    }

    public static List<string> EffectivePatterns(SqliteConnection connection, long rootId, SqliteTransaction? transaction = null)
    {
        using var command = connection.Command(
                "SELECT pattern FROM exclusions WHERE is_enabled = 1 AND (root_id IS NULL OR root_id = $root) ORDER BY root_id IS NOT NULL, sort_order, id;",
                transaction)
            .With("$root", rootId);
        using var reader = command.ExecuteReader();
        var patterns = new List<string>();
        while (reader.Read())
        {
            patterns.Add(reader.GetString(0));
        }

        return patterns;
    }

    private static List<WatchedRoot> ReadAll(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var roots = new List<WatchedRoot>();
        while (reader.Read())
        {
            roots.Add(new WatchedRoot(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetBool(2),
                reader.GetBool(3),
                reader.GetTime(4),
                reader.GetNullableTime(5),
                reader.GetNullableTime(6),
                reader.GetNullableInt64(7),
                reader.GetNullableTime(8),
                reader.GetNullableTime(9),
                reader.GetInt32(10),
                reader.GetNullableString(11)));
        }

        return roots;
    }
}
