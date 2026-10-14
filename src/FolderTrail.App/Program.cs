using Avalonia;
using FolderTrail.App.Composition;
using FolderTrail.App.Shell;
using FolderTrail.Core.Features.Settings;
using FolderTrail.Core.SharedKernel;
using FolderTrail.Core.SharedKernel.Diagnostics;
using FolderTrail.Core.SharedKernel.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FolderTrail.App;

public sealed record StartupOptions(bool StartMinimized, SingleInstance? Instance, Exception? StartupError)
{
    public static StartupOptions Design { get; } = new(false, null, null);
}

internal static class Program
{
    /// <summary>
    /// Startup: single-instance check, database migration, background services, then the UI loop.
    /// Shutdown (after the UI loop ends): stop the host, which stops watchers and flushes queued changes.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        using var instance = SingleInstance.Acquire();
        if (!instance.IsPrimary)
        {
            instance.SignalPrimary(TimeSpan.FromSeconds(3));
            return 0;
        }

        var location = AppDataLocation.ForCurrentUser();
        using var host = AppHost.Build(location);
        var log = host.Services.GetRequiredService<DiagnosticLog>();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Error("App", "Unhandled error.", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Error("App", "Unobserved background error.", e.Exception);
            e.SetObserved();
        };

        Exception? startupError = null;
        try
        {
            host.Services.GetRequiredService<AppDatabase>().Initialize();
            host.Start();
        }
        catch (Exception exception)
        {
            // Startup is the outermost boundary: show the reason in a window instead of exiting silently.
            log.Error("App", "FolderTrail couldn't open its data.", exception);
            startupError = exception;
        }

        var startMinimized = args.Contains(StartupArguments.Minimized, StringComparer.OrdinalIgnoreCase)
            || (PackageIdentity.IsPackaged && PackagedStartupRegistration.WasLaunchedByStartupTask());
        BuildAvaloniaApp(host.Services, new StartupOptions(startMinimized, instance, startupError))
            .StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);

        if (startupError is null)
        {
            // Continuations must not be posted to the UI dispatcher, which has stopped by now.
            Task.Run(() => host.StopAsync()).GetAwaiter().GetResult();
        }

        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(null, StartupOptions.Design);

    private static AppBuilder BuildAvaloniaApp(IServiceProvider? services, StartupOptions options) =>
        AppBuilder.Configure(() => new App(services, options))
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
