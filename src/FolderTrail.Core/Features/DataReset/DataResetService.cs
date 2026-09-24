using FolderTrail.Core.Features.Monitoring;
using FolderTrail.Core.Features.Settings;
using FolderTrail.Core.Features.WatchedFolders;
using FolderTrail.Core.SharedKernel.Diagnostics;
using FolderTrail.Core.SharedKernel.Persistence;

namespace FolderTrail.Core.Features.DataReset;

/// <summary>
/// Deletes everything FolderTrail stored: watched folders, history, inventory, gaps, exclusions, settings,
/// diagnostic logs and the Start with Windows entry. Monitoring is stopped first and restarted empty.
/// </summary>
public sealed class DataResetService(
    AppDatabase database,
    MonitoringCoordinator monitoring,
    SettingsService settings,
    WatchedFolderService folders,
    DiagnosticLog log)
{
    public event EventHandler? DataDeleted;

    public async Task DeleteAllDataAsync(CancellationToken cancellationToken = default)
    {
        await monitoring.StopAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await database.WriteAsync(
                    (connection, transaction) => connection.Execute(
                        """
                        DELETE FROM activity_events;
                        DELETE FROM inventory;
                        DELETE FROM monitoring_gaps;
                        DELETE FROM exclusions;
                        DELETE FROM watched_roots;
                        DELETE FROM app_settings;
                        """,
                        transaction),
                    cancellationToken)
                .ConfigureAwait(false);
            await database.VacuumAsync(cancellationToken).ConfigureAwait(false);

            log.Clear();
            log.DeleteLogFiles();

            await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (settings.Current.StartWithWindows)
            {
                await settings.UpdateAsync(s => s with { StartWithWindows = false }, cancellationToken).ConfigureAwait(false);
            }

            await folders.EnsureDefaultExclusionsAsync(cancellationToken).ConfigureAwait(false);
            log.Info("Data", "All stored data was deleted at the user's request.");
        }
        finally
        {
            await monitoring.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        DataDeleted?.Invoke(this, EventArgs.Empty);
    }
}
