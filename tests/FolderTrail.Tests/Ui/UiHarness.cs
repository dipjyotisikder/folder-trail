using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Logging;
using Avalonia.Threading;
using FolderTrail.App;
using FolderTrail.App.Composition;
using FolderTrail.App.Shared.Platform;
using FolderTrail.App.Shell;
using FolderTrail.Core.Features.Monitoring;
using FolderTrail.Core.Features.Settings;
using FolderTrail.Core.SharedKernel;
using FolderTrail.Core.SharedKernel.Persistence;
using FolderTrail.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

[assembly: AvaloniaTestApplication(typeof(FolderTrail.Tests.Ui.TestAppBuilder))]

namespace FolderTrail.Tests.Ui;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>Runs the real composition root and main window headlessly, with fakes only at the OS edges.</summary>
public sealed class UiHarness : IAsyncDisposable
{
    private UiHarness(string? dataParent)
    {
        Data = new TempDirectory(dataParent);
    }

    public TempDirectory Data { get; }

    public FakeFolderPicker Picker { get; } = new();

    public FakeSavePicker SavePicker { get; } = new();

    public FakeClipboard Clipboard { get; } = new();

    public FakeShellLauncher ShellLauncher { get; } = new();

    public RecordingNotifier Notifier { get; } = new();

    public IHost Host { get; private set; } = null!;

    public ShellViewModel Shell { get; private set; } = null!;

    public MainWindow Window { get; private set; } = null!;

    public T Get<T>()
        where T : notnull => Host.Services.GetRequiredService<T>();

    /// <param name="dataParent">Where to keep the app data; defaults to the temp folder. Screenshots pass a neutral path.</param>
    public static async Task<UiHarness> StartAsync(string? dataParent = null)
    {
        var harness = new UiHarness(dataParent);
        var location = new AppDataLocation(harness.Data.Path);
        harness.Host = AppHost.Build(location, services =>
        {
            services.AddSingleton(sp => new AppDatabase(new DatabaseOptions(location.DatabasePath, Pooling: false), sp.GetRequiredService<TimeProvider>()));
            services.AddSingleton<IFolderPicker>(harness.Picker);
            services.AddSingleton<IFileSavePicker>(harness.SavePicker);
            services.AddSingleton<IClipboardService>(harness.Clipboard);
            services.AddSingleton<IShellLauncher>(harness.ShellLauncher);
            services.AddSingleton<INotifier>(harness.Notifier);
            services.AddSingleton<IStartupRegistration>(new FakeStartupRegistration());
            services.AddSingleton(new MonitoringOptions
            {
                TimerResolution = TimeSpan.FromMilliseconds(200),
                AvailabilityCheckInterval = TimeSpan.FromSeconds(1),
            });
        });
        harness.Get<AppDatabase>().Initialize();
        await harness.Host.StartAsync(TestContext.Current.CancellationToken);

        harness.Shell = harness.Get<ShellViewModel>();
        harness.Window = new MainWindow { DataContext = harness.Shell, Width = 1320, Height = 840 };
        harness.Get<AvaloniaPlatform>().Attach(harness.Window);
        harness.Window.Show();
        harness.Shell.Initialize();
        await UiWait.UntilAsync(() => harness.Get<MonitoringCoordinator>().Initialized.IsCompleted, "monitoring is initialized");
        return harness;
    }

    public async ValueTask DisposeAsync()
    {
        Window.Close();
        await Host.StopAsync(CancellationToken.None);
        Host.Dispose();
        SqliteConnection.ClearAllPools();
        Data.Dispose();
    }
}

public static class UiWait
{
    public static async Task UntilAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(condition(), "Timed out waiting until " + because);
    }

    public static async Task SettleAsync(int milliseconds = 150)
    {
        await Task.Delay(milliseconds);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}

public sealed class FakeFolderPicker : IFolderPicker
{
    public string? NextPath { get; set; }

    public Task<string?> PickFolderAsync(string title) => Task.FromResult(NextPath);
}

public sealed class FakeSavePicker : IFileSavePicker
{
    public string? TargetPath { get; set; }

    public SaveFileRequest? LastRequest { get; private set; }

    public Task<(Stream Stream, string FileName)?> PickSaveFileAsync(SaveFileRequest request)
    {
        LastRequest = request;
        return Task.FromResult<(Stream, string)?>(TargetPath is null ? null : (File.Create(TargetPath), Path.GetFileName(TargetPath)));
    }
}

public sealed class FakeClipboard : IClipboardService
{
    public string? Text { get; private set; }

    public Task SetTextAsync(string text)
    {
        Text = text;
        return Task.CompletedTask;
    }
}

public sealed class FakeShellLauncher : IShellLauncher
{
    public ConcurrentQueue<string> Opened { get; } = new();

    public ConcurrentQueue<string> Revealed { get; } = new();

    public bool ItemExists(string path) => File.Exists(path) || Directory.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void Open(string path) => Opened.Enqueue(path);

    public void RevealInExplorer(string path) => Revealed.Enqueue(path);
}

public sealed class RecordingNotifier : INotifier
{
    public ConcurrentQueue<(string Title, string Message, NoticeKind Kind)> Notices { get; } = new();

    public void Notify(string title, string message, NoticeKind kind = NoticeKind.Information) => Notices.Enqueue((title, message, kind));
}

/// <summary>Collects Avalonia binding and property errors so a view with a broken binding fails its test.</summary>
public sealed class BindingErrorSink : ILogSink, IDisposable
{
    private readonly ILogSink? _previous;

    public BindingErrorSink()
    {
        _previous = Logger.Sink;
        Logger.Sink = this;
    }

    public ConcurrentQueue<string> Errors { get; } = new();

    public bool IsEnabled(LogEventLevel level, string area) =>
        level >= LogEventLevel.Warning && area is LogArea.Binding or LogArea.Property;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        Errors.Enqueue($"{area}: {messageTemplate} ({source})");

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
        Errors.Enqueue($"{area}: {messageTemplate} [{string.Join(", ", propertyValues)}] ({source})");

    public void Dispose() => Logger.Sink = _previous;
}
