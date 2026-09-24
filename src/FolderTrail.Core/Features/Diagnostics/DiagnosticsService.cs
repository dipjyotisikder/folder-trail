using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using FolderTrail.Core.Features.Monitoring;
using FolderTrail.Core.Features.Reconciliation;
using FolderTrail.Core.SharedKernel;
using FolderTrail.Core.SharedKernel.Diagnostics;
using FolderTrail.Core.SharedKernel.Persistence;

namespace FolderTrail.Core.Features.Diagnostics;

public sealed record DiagnosticsSnapshot(
    string AppVersion,
    string RuntimeVersion,
    string OperatingSystem,
    string DataDirectory,
    string DatabasePath,
    long DatabaseSizeBytes,
    int SchemaVersion,
    long ActivityRecordCount,
    long InventoryItemCount,
    int OpenGapCount,
    IReadOnlyList<RootRuntimeStatus> Folders,
    IReadOnlyList<DiagnosticEntry> RecentEntries,
    DateTimeOffset CapturedUtc);

public sealed class DiagnosticsService(
    AppDatabase database,
    AppDataLocation location,
    MonitoringCoordinator monitoring,
    DiagnosticLog log,
    TimeProvider time)
{
    public const string CompletenessStatement =
        "A healthy live watcher means FolderTrail is receiving change notifications. It does not prove that every " +
        "change was captured: notifications can be lost, and changes made while FolderTrail wasn't watching are " +
        "only found later by scans, as net differences without their exact times.";

    public static string AppVersion =>
        typeof(DiagnosticsService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(DiagnosticsService).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public async Task<DiagnosticsSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var (events, inventory, gaps) = await database.ReadAsync(
                connection =>
                {
                    using var eventCount = connection.Command("SELECT COUNT(*) FROM activity_events;");
                    using var gapCount = connection.Command(
                        "SELECT COUNT(*) FROM monitoring_gaps g JOIN watched_roots r ON r.id = g.root_id WHERE g.end_utc IS NULL AND r.removed_utc IS NULL;");
                    return (eventCount.ScalarInt64(), InventoryStore.Count(connection), (int)gapCount.ScalarInt64());
                },
                cancellationToken)
            .ConfigureAwait(false);

        return new DiagnosticsSnapshot(
            AppVersion,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription + " (" + RuntimeInformation.OSArchitecture + ")",
            location.RootDirectory,
            database.DatabasePath,
            database.GetStorageSizeBytes(),
            database.SchemaVersion,
            events,
            inventory,
            gaps,
            monitoring.GetStatuses(),
            log.Snapshot(),
            time.GetUtcNow());
    }

    /// <summary>A plain-text report. Paths appear only when <paramref name="includePaths"/> is explicitly chosen.</summary>
    public static string BuildReport(DiagnosticsSnapshot snapshot, bool includePaths)
    {
        string Path(string value) => includePaths ? value : PathRedactor.Placeholder;
        var builder = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;
        builder.AppendLine("FolderTrail diagnostics report");
        builder.AppendLine(culture, $"Captured (UTC): {snapshot.CapturedUtc:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine(culture, $"Paths included: {(includePaths ? "yes" : "no")}");
        builder.AppendLine();
        builder.AppendLine(culture, $"App version: {snapshot.AppVersion}");
        builder.AppendLine(culture, $"Runtime: {snapshot.RuntimeVersion}");
        builder.AppendLine(culture, $"Operating system: {snapshot.OperatingSystem}");
        builder.AppendLine(culture, $"Data folder: {Path(snapshot.DataDirectory)}");
        builder.AppendLine(culture, $"Database: {Path(snapshot.DatabasePath)}");
        builder.AppendLine(culture, $"Database size: {snapshot.DatabaseSizeBytes:N0} bytes, schema version {snapshot.SchemaVersion}");
        builder.AppendLine(culture, $"Activity records: {snapshot.ActivityRecordCount:N0}; inventory items: {snapshot.InventoryItemCount:N0}; open monitoring gaps: {snapshot.OpenGapCount}");
        builder.AppendLine();
        builder.AppendLine(CompletenessStatement);
        builder.AppendLine();
        builder.AppendLine(culture, $"Watched folders ({snapshot.Folders.Count}):");
        var index = 1;
        foreach (var folder in snapshot.Folders)
        {
            builder.AppendLine(culture, $"  {index++}. {(includePaths ? folder.DisplayPath : "Folder #" + folder.RootId.ToString(culture))}");
            builder.AppendLine(culture, $"     State: {RootRuntimeStatus.Describe(folder.State)}; live watcher: {(folder.IsLiveWatcherActive ? "running" : "not running")}; subfolders: {(folder.IncludeSubfolders ? "yes" : "no")}");
            builder.AppendLine(culture, $"     Last successful scan: {Format(folder.LastSuccessfulScanUtc)}; last observed event: {Format(folder.LastEventUtc)}");
            builder.AppendLine(culture, $"     Baseline items: {folder.BaselineItemCount?.ToString("N0", culture) ?? "-"}; skipped in last scan: {folder.LastScanSkippedCount}; open gaps: {folder.OpenGapCount}");
            if (folder.Message is { Length: > 0 } message)
            {
                builder.AppendLine(culture, $"     Note: {(includePaths ? message : PathRedactor.Redact(message))}");
            }
        }

        builder.AppendLine();
        builder.AppendLine(culture, $"Recent diagnostic entries ({snapshot.RecentEntries.Count}, newest first):");
        foreach (var entry in snapshot.RecentEntries)
        {
            builder.AppendLine(culture, $"  {entry.TimestampUtc:yyyy-MM-dd HH:mm:ss}Z {entry.Severity} {entry.Area}: {entry.Render(includePaths)}");
        }

        return builder.ToString();
    }

    private static string Format(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "never";
}
