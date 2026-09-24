using FolderTrail.Core.Features.Settings;
using FolderTrail.Core.SharedKernel.Diagnostics;
using FolderTrail.Core.SharedKernel.Persistence;

namespace FolderTrail.Core.Features.Retention;

public sealed record RetentionResult(int EventsDeleted, int GapsDeleted, int RemovedFoldersForgotten);

/// <summary>
/// Deletes activity history older than the chosen retention period. The inventory is never touched: it's the
/// current state of watched folders that scans compare against, and it's kept for as long as a folder is watched.
/// </summary>
public sealed class RetentionService(AppDatabase database, SettingsService settings, TimeProvider time, DiagnosticLog log)
{
    private const int BatchSize = 5_000;

    private readonly SemaphoreSlim _wake = new(0, 1);

    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(6);

    public async Task<RetentionResult> PruneAsync(CancellationToken cancellationToken = default)
    {
        if (settings.Current.RetentionDays is not { } days)
        {
            return new RetentionResult(0, 0, 0);
        }

        var cutoff = UnixTime.ToMs(time.GetUtcNow() - TimeSpan.FromDays(days));
        var events = 0;
        int deleted;
        do
        {
            // Small batches keep each write transaction short so live events aren't delayed.
            deleted = await database.WriteAsync(
                    (connection, transaction) =>
                    {
                        using var command = connection.Command(
                            "DELETE FROM activity_events WHERE id IN (SELECT id FROM activity_events WHERE detected_utc < $cutoff LIMIT $batch);",
                            transaction);
                        return command.With("$cutoff", cutoff).With("$batch", BatchSize).ExecuteNonQuery();
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            events += deleted;
        }
        while (deleted == BatchSize);

        var (gaps, forgotten) = await database.WriteAsync(
                (connection, transaction) =>
                {
                    using var gapCommand = connection.Command(
                        "DELETE FROM monitoring_gaps WHERE end_utc IS NOT NULL AND end_utc < $cutoff;",
                        transaction);
                    var gapCount = gapCommand.With("$cutoff", cutoff).ExecuteNonQuery();

                    using var rootCommand = connection.Command(
                        """
                        DELETE FROM watched_roots
                        WHERE removed_utc IS NOT NULL
                          AND NOT EXISTS (SELECT 1 FROM activity_events e WHERE e.root_id = watched_roots.id);
                        """,
                        transaction);
                    return (gapCount, rootCommand.ExecuteNonQuery());
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (events > 0 || gaps > 0)
        {
            log.Info("Retention", $"Deleted {events} activity record(s) and {gaps} monitoring gap(s) older than {days} days.");
        }

        return new RetentionResult(events, gaps, forgotten);
    }

    /// <summary>Asks the background loop to prune now, for example after the retention period changed.</summary>
    public void RequestPrune()
    {
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var retention = settings.Current.RetentionDays;
        settings.Changed += OnSettingsChanged;
        try
        {
            await WaitAsync(InitialDelay, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await PruneAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    log.Error("Retention", "Cleaning up old history failed; it will be retried later.", exception);
                }

                await WaitAsync(Interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            settings.Changed -= OnSettingsChanged;
        }

        void OnSettingsChanged(object? sender, AppSettings updated)
        {
            if (updated.RetentionDays != retention)
            {
                retention = updated.RetentionDays;
                RequestPrune();
            }
        }
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        await _wake.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
}
