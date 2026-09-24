using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using FolderTrail.App.Shared.Platform;
using FolderTrail.Core.Features.Monitoring;
using FolderTrail.Core.Features.Settings;
using FolderTrail.Core.Features.WatchedFolders;
using FolderTrail.Core.SharedKernel.Diagnostics;

namespace FolderTrail.App.Shell;

/// <summary>Main-window lifecycle: close either quits or hides to the notification area, as the user chose.</summary>
public sealed class MainWindowController : IMainWindowController
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly Window _window;
    private readonly SettingsService _settings;
    private bool _quitting;

    public MainWindowController(IClassicDesktopStyleApplicationLifetime desktop, Window window, SettingsService settings)
    {
        _desktop = desktop;
        _window = window;
        _settings = settings;
        window.Closing += OnClosing;
        window.Closed += (_, _) => RequestQuit();
    }

    public bool IsQuitting => _quitting;

    public void ShowAndActivate()
    {
        if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    public void ShowMinimized()
    {
        _window.WindowState = WindowState.Minimized;
        _window.Show();
    }

    public void RequestQuit()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        _desktop.Shutdown();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_quitting)
        {
            return;
        }

        var userClosedWindow = e.CloseReason == WindowCloseReason.WindowClosing && !e.IsProgrammatic;
        if (userClosedWindow && _settings.Current.CloseToTray)
        {
            e.Cancel = true;
            _window.Hide();
            return;
        }

        e.Cancel = true;
        RequestQuit();
    }
}

/// <summary>The notification-area icon: open, pause/resume everything, scan now, and quit.</summary>
public sealed class TrayIconController : IDisposable
{
    private readonly TrayIcon _tray;
    private readonly MonitoringCoordinator _monitoring;
    private readonly IUiDispatcher _ui;

    public TrayIconController(
        Application application,
        IMainWindowController window,
        WatchedFolderService folders,
        MonitoringCoordinator monitoring,
        IUiDispatcher ui,
        INotifier notifier,
        DiagnosticLog log)
    {
        _monitoring = monitoring;
        _ui = ui;

        NativeMenuItem Item(string header, Func<Task> action)
        {
            var item = new NativeMenuItem(header);
            item.Click += async (_, _) =>
            {
                try
                {
                    await action();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    log.Error("Tray", header + " failed.", exception);
                    notifier.Notify(header + " failed", exception.Message, NoticeKind.Error);
                }
            };
            return item;
        }

        var menu = new NativeMenu
        {
            Item("Open FolderTrail", () =>
            {
                window.ShowAndActivate();
                return Task.CompletedTask;
            }),
            new NativeMenuItemSeparator(),
            Item("Pause all", () => folders.PauseAllAsync()),
            Item("Resume all", () => folders.ResumeAllAsync()),
            Item("Scan now", () =>
            {
                monitoring.RequestScan();
                return Task.CompletedTask;
            }),
            new NativeMenuItemSeparator(),
            Item("Quit", () =>
            {
                window.RequestQuit();
                return Task.CompletedTask;
            }),
        };

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://FolderTrail/Assets/foldertrail.ico"))),
            ToolTipText = "FolderTrail",
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => window.ShowAndActivate();
        TrayIcon.SetIcons(application, [_tray]);
        monitoring.StatusChanged += OnStatusChanged;
        UpdateToolTip();
    }

    public void Dispose()
    {
        _monitoring.StatusChanged -= OnStatusChanged;
        _tray.IsVisible = false;
        _tray.Dispose();
    }

    private void OnStatusChanged(object? sender, RootRuntimeStatus status) => _ui.Post(UpdateToolTip);

    private void UpdateToolTip()
    {
        var statuses = _monitoring.GetStatuses();
        var problems = statuses.Count(s => s.State is RootState.Missing or RootState.NeedsAttention);
        var paused = statuses.Count(s => s.State == RootState.Paused);
        var text = statuses.Count switch
        {
            0 => "FolderTrail: no folders watched",
            1 => "FolderTrail: 1 folder",
            _ => $"FolderTrail: {statuses.Count} folders",
        };
        if (problems > 0)
        {
            text += $", {problems} need{(problems == 1 ? "s" : string.Empty)} attention";
        }
        else if (paused > 0)
        {
            text += $", {paused} paused";
        }

        _tray.ToolTipText = text;
    }
}
