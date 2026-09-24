using FolderTrail.App.Features.Diagnostics;
using FolderTrail.App.Features.Onboarding;
using FolderTrail.App.Features.Settings;
using FolderTrail.App.Features.Timeline;
using FolderTrail.App.Features.WatchedFolders;
using FolderTrail.App.Shared.Dialogs;
using FolderTrail.App.Shared.Platform;
using FolderTrail.App.Shell;
using FolderTrail.Core.Features.DataReset;
using FolderTrail.Core.Features.Diagnostics;
using FolderTrail.Core.Features.Monitoring;
using FolderTrail.Core.Features.Reconciliation;
using FolderTrail.Core.Features.Retention;
using FolderTrail.Core.Features.Settings;
using FolderTrail.Core.Features.Timeline;
using FolderTrail.Core.Features.WatchedFolders;
using FolderTrail.Core.SharedKernel;
using FolderTrail.Core.SharedKernel.Diagnostics;
using FolderTrail.Core.SharedKernel.FileSystem;
using FolderTrail.Core.SharedKernel.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FolderTrail.App.Composition;

/// <summary>The composition root. Everything is a singleton: there is one database, one pipeline and one window.</summary>
public static class AppHost
{
    public static IHost Build(AppDataLocation location, Action<IServiceCollection>? overrides = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ApplicationName = "FolderTrail",
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(20);
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
        });

        AddCore(builder.Services, location);
        AddUi(builder.Services);
        builder.Services.AddHostedService<MonitoringHostedService>();
        overrides?.Invoke(builder.Services);
        return builder.Build();
    }

    private static void AddCore(IServiceCollection services, AppDataLocation location)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(location);
        services.AddSingleton(sp => new DiagnosticLog(sp.GetRequiredService<TimeProvider>(), location.LogDirectory));
        services.AddSingleton(sp => new AppDatabase(new DatabaseOptions(location.DatabasePath), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<KeyValueStore>();

        services.AddSingleton<IFolderProbe, LocalFolderProbe>();
        services.AddSingleton<IItemInspector, LocalItemInspector>();
        services.AddSingleton<IFileSystemScanner, FileSystemScanner>();
        services.AddSingleton<IDirectoryWatcherFactory, FileSystemWatcherFactory>();
        services.AddSingleton<IStartupRegistration>(_ =>
            OperatingSystem.IsWindows() && Environment.ProcessPath is { } executable
                ? new WindowsStartupRegistration(executable)
                : new UnsupportedStartupRegistration());

        services.AddSingleton<SettingsService>();
        services.AddSingleton<WatchedFolderService>();
        services.AddSingleton<ReconciliationService>();
        services.AddSingleton<LiveEventApplier>();
        services.AddSingleton(sp => new LiveEventProcessor(
            sp.GetRequiredService<LiveEventApplier>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<DiagnosticLog>()));
        services.AddSingleton<MonitoringGapStore>();
        services.AddSingleton(new MonitoringOptions());
        services.AddSingleton<MonitoringCoordinator>();
        services.AddSingleton<TimelineService>();
        services.AddSingleton<RetentionService>();
        services.AddSingleton<DiagnosticsService>();
        services.AddSingleton<DataResetService>();
    }

    private static void AddUi(IServiceCollection services)
    {
        services.AddSingleton<AvaloniaPlatform>();
        services.AddSingleton<IUiDispatcher>(sp => sp.GetRequiredService<AvaloniaPlatform>());
        services.AddSingleton<IFolderPicker>(sp => sp.GetRequiredService<AvaloniaPlatform>());
        services.AddSingleton<IFileSavePicker>(sp => sp.GetRequiredService<AvaloniaPlatform>());
        services.AddSingleton<IClipboardService>(sp => sp.GetRequiredService<AvaloniaPlatform>());
        services.AddSingleton<IShellLauncher>(sp => sp.GetRequiredService<AvaloniaPlatform>());
        services.AddSingleton<INotifier>(sp => sp.GetRequiredService<AvaloniaPlatform>());

        services.AddSingleton<DialogHostViewModel>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogHostViewModel>());
        services.AddSingleton<WatchedFoldersViewModel>();
        services.AddSingleton<EventDetailsViewModel>();
        services.AddSingleton<TimelineViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DiagnosticsViewModel>();
        services.AddSingleton<OnboardingViewModel>();
        services.AddSingleton<ShellViewModel>();
    }
}

/// <summary>
/// Explicit background lifecycle. Start: load settings, seed default exclusions, start monitoring and retention.
/// Stop: stop watchers, write every queued change, and cancel retention.
/// </summary>
public sealed class MonitoringHostedService(
    SettingsService settings,
    WatchedFolderService folders,
    MonitoringCoordinator monitoring,
    RetentionService retention,
    DiagnosticLog log) : IHostedService
{
    private readonly CancellationTokenSource _stopping = new();
    private Task? _retentionLoop;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await settings.LoadAsync(cancellationToken);
        await folders.EnsureDefaultExclusionsAsync(cancellationToken);
        await monitoring.StartAsync(cancellationToken);
        _retentionLoop = Task.Run(() => retention.RunAsync(_stopping.Token), CancellationToken.None);
        log.Info("App", $"FolderTrail {DiagnosticsService.AppVersion} started.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        await monitoring.StopAsync(CancellationToken.None);
        if (_retentionLoop is not null)
        {
            await _retentionLoop.WaitAsync(cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }
}
