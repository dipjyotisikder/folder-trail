using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using FolderTrail.App.Shared.Platform;
using FolderTrail.App.Shell;
using FolderTrail.Core.Features.Monitoring;
using FolderTrail.Core.Features.Settings;
using FolderTrail.Core.Features.WatchedFolders;
using FolderTrail.Core.SharedKernel;
using FolderTrail.Core.SharedKernel.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace FolderTrail.App;

public partial class App : Application
{
    private readonly IServiceProvider? _services;
    private readonly StartupOptions _options;
    private TrayIconController? _tray;

    public App()
        : this(null, StartupOptions.Design)
    {
    }

    public App(IServiceProvider? services, StartupOptions options)
    {
        _services = services;
        _options = options;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && _services is not null)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            if (_options.StartupError is { } error)
            {
                ShowStartupError(desktop, error);
            }
            else
            {
                StartShell(desktop, _services);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static void ApplyTheme(ThemePreference theme)
    {
        if (Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                ThemePreference.Light => ThemeVariant.Light,
                ThemePreference.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }

    private void StartShell(IClassicDesktopStyleApplicationLifetime desktop, IServiceProvider services)
    {
        var settings = services.GetRequiredService<SettingsService>();
        var log = services.GetRequiredService<DiagnosticLog>();
        var platform = services.GetRequiredService<AvaloniaPlatform>();
        ApplyTheme(settings.Current.Theme);
        settings.Changed += (_, updated) => Dispatcher.UIThread.Post(() => ApplyTheme(updated.Theme));
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            log.Error("UI", "Unexpected error in the user interface.", e.Exception);
            platform.Notify("Something went wrong", "FolderTrail hit an unexpected error. Details are in Diagnostics.", NoticeKind.Error);
            e.Handled = true;
        };

        var shell = services.GetRequiredService<ShellViewModel>();
        var window = new MainWindow { DataContext = shell };
        platform.Attach(window);
        var controller = new MainWindowController(desktop, window, settings);
        _tray = new TrayIconController(
            this,
            controller,
            services.GetRequiredService<WatchedFolderService>(),
            services.GetRequiredService<MonitoringCoordinator>(),
            platform,
            platform,
            log);
        desktop.Exit += (_, _) => _tray?.Dispose();
        _options.Instance?.StartListening(() => Dispatcher.UIThread.Post(controller.ShowAndActivate));

        shell.Initialize();
        if (!_options.StartMinimized)
        {
            controller.ShowAndActivate();
        }
        else if (!settings.Current.CloseToTray)
        {
            controller.ShowMinimized();
        }
    }

    private static void ShowStartupError(IClassicDesktopStyleApplicationLifetime desktop, Exception error)
    {
        var window = new StartupErrorWindow(error.Message, AppDataLocator.Resolve().RootDirectory);
        window.Closed += (_, _) => desktop.Shutdown();
        window.Show();
    }
}
