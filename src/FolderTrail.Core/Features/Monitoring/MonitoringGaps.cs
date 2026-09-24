using FolderTrail.Core.SharedKernel.Persistence;

namespace FolderTrail.Core.Features.Monitoring;

public enum GapReason
{
    AppNotRunning = 1,
    Paused = 2,
    PathUnavailable = 3,
    WatcherError = 4,
    EventOverflow = 5,
    SystemSuspended = 6,
}

/// <summary>
/// A period in which live changes weren't being observed. The scan that ends it records net differences only:
/// intermediate actions and exact change times during a gap can't be reconstructed.
/// </summary>
public sealed record MonitoringGap(long Id, long RootId, GapReason Reason, DateTimeOffset? StartUtc, DateTimeOffset? EndUtc, string? Detail)
{
    public bool IsOpen => EndUtc is null;

    public static string Describe(GapReason reason) => reason switch
    {
        GapReason.AppNotRunning => "FolderTrail wasn't running",
        GapReason.Paused => "Monitoring was paused",
        GapReason.PathUnavailable => "The folder was unavailable",
        GapReason.WatcherError => "Live monitoring stopped after an error",
        GapReason.EventOverflow => "Too many changes at once; some live notifications were lost",
        GapReason.SystemSuspended => "The computer was asleep",
        _ => "Monitoring gap",
    };
}

public sealed class MonitoringGapStore(AppDatabase database)
{
    /// <returns>False when a gap with the same reason is already open (its original start is kept).</returns>
    public Task<bool> OpenAsync(long rootId, GapReason reason, DateTimeOffset? startUtc, string? detail, CancellationToken cancellationToken = default) =>
        database.WriteAsync(
            (connection, transaction) =>
            {
                using (var existing = connection.Command(
                           "SELECT COUNT(*) FROM monitoring_gaps WHERE root_id = $root AND reason = $reason AND end_utc IS NULL;",
                           transaction))
                {
                    if (existing.With("$root", rootId).With("$reason", (int)reason).ScalarInt64() > 0)
                    {
                        return false;
                    }
                }

                if (!WatchedRootExists(connection, transaction, rootId))
                {
                    return false;
                }

                using var insert = connection.Command(
                    "INSERT INTO monitoring_gaps (root_id, reason, start_utc, detail) VALUES ($root, $reason, $start, $detail);",
                    transaction);
                insert.With("$root", rootId)
                    .With("$reason", (int)reason)
                    .With("$start", UnixTime.ToMs(startUtc))
                    .With("$detail", detail)
                    .ExecuteNonQuery();
                return true;
            },
            cancellationToken);

    public Task<int> CloseOpenAsync(long rootId, DateTimeOffset endUtc, CancellationToken cancellationToken = default) =>
        database.WriteAsync(
            (connection, transaction) =>
            {
                using var command = connection.Command(
                    "UPDATE monitoring_gaps SET end_utc = MAX($end, COALESCE(start_utc, $end)) WHERE root_id = $root AND end_utc IS NULL;",
                    transaction);
                return command.With("$end", UnixTime.ToMs(endUtc)).With("$root", rootId).ExecuteNonQuery();
            },
            cancellationToken);

    public Task<int> CountOpenAsync(long rootId, CancellationToken cancellationToken = default) =>
        database.ReadAsync(
            connection =>
            {
                using var command = connection.Command("SELECT COUNT(*) FROM monitoring_gaps WHERE root_id = $root AND end_utc IS NULL;");
                return (int)command.With("$root", rootId).ScalarInt64();
            },
            cancellationToken);

    public Task<IReadOnlyList<MonitoringGap>> GetRecentAsync(long rootId, int limit, CancellationToken cancellationToken = default) =>
        database.ReadAsync<IReadOnlyList<MonitoringGap>>(
            connection =>
            {
                using var command = connection.Command(
                    """
                    SELECT id, root_id, reason, start_utc, end_utc, detail FROM monitoring_gaps
                    WHERE root_id = $root
                    ORDER BY end_utc IS NOT NULL, COALESCE(end_utc, start_utc) DESC, id DESC
                    LIMIT $limit;
                    """);
                command.With("$root", rootId).With("$limit", limit);
                using var reader = command.ExecuteReader();
                var gaps = new List<MonitoringGap>();
                while (reader.Read())
                {
                    gaps.Add(new MonitoringGap(
                        reader.GetInt64(0),
                        reader.GetInt64(1),
                        (GapReason)reader.GetInt32(2),
                        reader.GetNullableTime(3),
                        reader.GetNullableTime(4),
                        reader.GetNullableString(5)));
                }

                return gaps;
            },
            cancellationToken);

    private static bool WatchedRootExists(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction, long rootId)
    {
        using var command = connection.Command("SELECT COUNT(*) FROM watched_roots WHERE id = $root AND removed_utc IS NULL;", transaction);
        return command.With("$root", rootId).ScalarInt64() > 0;
    }
}
