using FolderTrail.Core.Features.DataReset;
using FolderTrail.Core.Features.Diagnostics;
using FolderTrail.Core.Features.Monitoring;
using FolderTrail.Core.SharedKernel;
using FolderTrail.Tests.Features.WatchedFolders;
using FolderTrail.Tests.Support;

namespace FolderTrail.Tests.Features.DataReset;

public sealed class DataResetAndDiagnosticsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [WindowsFact]
    public async Task Deleting_all_data_leaves_a_fresh_app_with_monitoring_running()
    {
        using var folder = new TempDirectory();
        var watchers = new FakeWatcherFactory();
        using var harness = new CoreHarness(watchers: watchers);
        await harness.Folders.EnsureDefaultExclusionsAsync(Token);
        var root = await harness.AddRootAsync(folder.Path);
        await WatchedFolderServiceTests.InsertEventAsync(harness, root.Id, folder.Combine("a.txt"));
        await harness.Settings.UpdateAsync(s => s with { OnboardingCompleted = true, StartWithWindows = true }, Token);
        await harness.Monitoring.StartAsync(Token);
        await Eventually.TrueAsync(() => harness.Monitoring.GetStatus(root.Id)?.State == RootState.Watching, "the folder is watched");

        await new DataResetService(harness.Database, harness.Monitoring, harness.Settings, harness.Folders, harness.Log).DeleteAllDataAsync(Token);

        Assert.Empty(await harness.Folders.GetActiveAsync(Token));
        Assert.Empty(await harness.EventsAsync());
        Assert.False(harness.Settings.Current.OnboardingCompleted);
        Assert.False(harness.Startup.Enabled);
        Assert.NotEmpty(await harness.Folders.GetExclusionsAsync(null, Token));
        Assert.True(harness.Monitoring.IsRunning);
        Assert.Empty(harness.Monitoring.GetStatuses());
        Assert.True(watchers.Created.All(w => w.Disposed));
    }

    [WindowsFact]
    public async Task Diagnostics_report_hides_paths_unless_explicitly_included()
    {
        using var folder = new TempDirectory();
        using var harness = new CoreHarness(watchers: new FakeWatcherFactory());
        var root = await harness.AddRootAsync(folder.Path);
        await harness.Monitoring.StartAsync(Token);
        await Eventually.TrueAsync(() => harness.Monitoring.GetStatus(root.Id)?.State == RootState.Watching, "the folder is watched");
        harness.Log.Warning("Scan", $"Couldn't read '{folder.Combine("secret.txt")}'.");
        var service = new DiagnosticsService(harness.Database, new AppDataLocation(Path.GetDirectoryName(harness.Database.DatabasePath)!), harness.Monitoring, harness.Log, harness.Time);

        var snapshot = await service.CaptureAsync(Token);
        var redacted = DiagnosticsService.BuildReport(snapshot, includePaths: false);
        var full = DiagnosticsService.BuildReport(snapshot, includePaths: true);

        Assert.Single(snapshot.Folders);
        Assert.True(snapshot.DatabaseSizeBytes > 0);
        Assert.DoesNotContain(folder.Path, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret.txt", redacted, StringComparison.Ordinal);
        Assert.Contains(folder.Path, full, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secret.txt", full, StringComparison.Ordinal);
        Assert.Contains("does not prove that every change was captured", redacted, StringComparison.Ordinal);
    }
}
