using System.Runtime.CompilerServices;
using System.Text;
using FolderTrail.Core.SharedKernel.Journal;
using FolderTrail.Core.SharedKernel.Paths;
using FolderTrail.Core.SharedKernel.Persistence;
using Microsoft.Data.Sqlite;

namespace FolderTrail.Core.Features.Timeline;

/// <summary>Read side of the journal: newest-first, keyset-paged, fully parameterized queries.</summary>
public sealed class TimelineService(AppDatabase database)
{
    public const int DefaultPageSize = 200;

    private const string SelectColumns =
        """
        SELECT e.id, e.root_id, r.display_path, r.removed_utc IS NOT NULL, e.detected_utc, e.event_type, e.source, e.kind,
               e.path, e.name, e.old_path, e.length, e.last_write_utc, e.previous_length, e.previous_last_write_utc,
               e.previous_observed_utc, e.merged_count
        FROM activity_events e
        JOIN watched_roots r ON r.id = e.root_id
        """;

    public Task<TimelinePage> GetPageAsync(TimelineFilter filter, TimelineCursor? before = null, int pageSize = DefaultPageSize, CancellationToken cancellationToken = default) =>
        database.ReadAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                var where = BuildWhere(command, filter);
                if (before is not null)
                {
                    where.Add("(e.detected_utc < $cursorTime OR (e.detected_utc = $cursorTime AND e.id < $cursorId))");
                    command.With("$cursorTime", UnixTime.ToMs(before.DetectedUtc)).With("$cursorId", before.Id);
                }

                command.CommandText = $"{SelectColumns} {WhereClause(where)} ORDER BY e.detected_utc DESC, e.id DESC LIMIT $limit;";
                command.With("$limit", pageSize + 1);
                var items = Read(command);
                var hasMore = items.Count > pageSize;
                if (hasMore)
                {
                    items.RemoveAt(items.Count - 1);
                }

                return new TimelinePage(items, hasMore ? items[^1].Cursor : null);
            },
            cancellationToken);

    /// <summary>
    /// Entries stored after the entry with id <paramref name="afterId"/>, newest first. Ids only grow, so this finds
    /// everything added since, including entries dated earlier than what's shown (held or merged live events).
    /// </summary>
    public Task<IReadOnlyList<TimelineEntry>> GetAddedAfterAsync(TimelineFilter filter, long afterId, int max = 500, CancellationToken cancellationToken = default) =>
        database.ReadAsync<IReadOnlyList<TimelineEntry>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                var where = BuildWhere(command, filter);
                where.Add("e.id > $afterId");
                command.With("$afterId", afterId);
                command.CommandText = $"{SelectColumns} {WhereClause(where)} ORDER BY e.detected_utc DESC, e.id DESC LIMIT $limit;";
                command.With("$limit", max);
                return Read(command);
            },
            cancellationToken);

    public Task<long> CountAsync(TimelineFilter filter, CancellationToken cancellationToken = default) =>
        database.ReadAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                var where = BuildWhere(command, filter);
                command.CommandText = $"SELECT COUNT(*) FROM activity_events e {WhereClause(where)};";
                return command.ScalarInt64();
            },
            cancellationToken);

    public Task<TimelineEntry?> GetAsync(long eventId, CancellationToken cancellationToken = default) =>
        database.ReadAsync(
            connection =>
            {
                using var command = connection.Command($"{SelectColumns} WHERE e.id = $id;").With("$id", eventId);
                return Read(command).FirstOrDefault();
            },
            cancellationToken);

    /// <summary>Folders that have history, including removed folders whose history was kept.</summary>
    public Task<IReadOnlyList<TimelineRootOption>> GetRootOptionsAsync(CancellationToken cancellationToken = default) =>
        database.ReadAsync<IReadOnlyList<TimelineRootOption>>(
            connection =>
            {
                using var command = connection.Command(
                    """
                    SELECT id, display_path, removed_utc IS NOT NULL FROM watched_roots
                    WHERE removed_utc IS NULL OR EXISTS (SELECT 1 FROM activity_events e WHERE e.root_id = watched_roots.id)
                    ORDER BY removed_utc IS NOT NULL, display_path COLLATE NOCASE;
                    """);
                using var reader = command.ExecuteReader();
                var options = new List<TimelineRootOption>();
                while (reader.Read())
                {
                    options.Add(new TimelineRootOption(reader.GetInt64(0), reader.GetString(1), reader.GetBool(2)));
                }

                return options;
            },
            cancellationToken);

    public async IAsyncEnumerable<TimelineEntry> StreamAsync(TimelineFilter filter, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        TimelineCursor? cursor = null;
        do
        {
            var page = await GetPageAsync(filter, cursor, 1_000, cancellationToken).ConfigureAwait(false);
            foreach (var entry in page.Items)
            {
                yield return entry;
            }

            cursor = page.Next;
        }
        while (cursor is not null);
    }

    public async Task<int> ExportCsvAsync(TimelineFilter filter, Stream destination, TimeZoneInfo localZone, CancellationToken cancellationToken = default)
    {
        await using var writer = CsvExporter.CreateWriter(destination);
        var count = await CsvExporter.WriteAsync(writer, StreamAsync(filter, cancellationToken), localZone, cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    internal static List<string> BuildWhere(SqliteCommand command, TimelineFilter filter)
    {
        var where = new List<string>();
        var terms = (filter.SearchText ?? string.Empty)
            .Replace('/', WindowsPath.Separator)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < terms.Length; i++)
        {
            var name = $"$term{i}";
            where.Add($"(e.path_key LIKE {name} ESCAPE '|' OR e.old_path_key LIKE {name} ESCAPE '|')");
            command.With(name, "%" + EscapeLike(terms[i].ToUpperInvariant()) + "%");
        }

        AddInList(command, where, "e.root_id", "$root", filter.RootIds);
        AddInList(command, where, "e.event_type", "$type", filter.Types?.Select(t => (long)t).ToList());
        AddInList(command, where, "e.source", "$source", filter.Sources?.Select(s => (long)s).ToList());
        if (filter.FromUtc is { } from)
        {
            where.Add("e.detected_utc >= $from");
            command.With("$from", UnixTime.ToMs(from));
        }

        if (filter.ToUtc is { } to)
        {
            where.Add("e.detected_utc < $to");
            command.With("$to", UnixTime.ToMs(to));
        }

        return where;
    }

    internal static string EscapeLike(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '%' or '_' or '|')
            {
                builder.Append('|');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static void AddInList(SqliteCommand command, List<string> where, string column, string prefix, IReadOnlyCollection<long>? values)
    {
        if (values is null || values.Count == 0)
        {
            return;
        }

        var names = new List<string>(values.Count);
        var index = 0;
        foreach (var value in values)
        {
            var name = $"{prefix}{index++}";
            names.Add(name);
            command.With(name, value);
        }

        where.Add($"{column} IN ({string.Join(", ", names)})");
    }

    private static string WhereClause(List<string> where) => where.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", where);

    private static List<TimelineEntry> Read(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var entries = new List<TimelineEntry>();
        while (reader.Read())
        {
            entries.Add(new TimelineEntry(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetBool(3),
                reader.GetTime(4),
                (ActivityType)reader.GetInt32(5),
                (ActivitySource)reader.GetInt32(6),
                (ItemKind)reader.GetInt32(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetNullableString(10),
                reader.GetNullableInt64(11),
                reader.GetNullableTime(12),
                reader.GetNullableInt64(13),
                reader.GetNullableTime(14),
                reader.GetNullableTime(15),
                reader.GetInt32(16)));
        }

        return entries;
    }
}
