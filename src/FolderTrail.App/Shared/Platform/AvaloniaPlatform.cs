using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FolderTrail.Core.SharedKernel.Diagnostics;

namespace FolderTrail.App.Shared.Platform;

/// <summary>Implements the platform abstractions against the main window once it exists.</summary>
public sealed class AvaloniaPlatform(DiagnosticLog log) : IUiDispatcher, IFolderPicker, IFileSavePicker, IClipboardService, IShellLauncher, INotifier
{
    private TopLevel? _topLevel;
    private WindowNotificationManager? _notifications;

    public void Attach(TopLevel topLevel)
    {
        _topLevel = topLevel;
        _notifications = new WindowNotificationManager(topLevel)
        {
            Position = NotificationPosition.BottomRight,
            MaxItems = 3,
        };
    }

    public void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (_topLevel is null)
        {
            return null;
        }

        var folders = await _topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    public async Task<(Stream Stream, string FileName)?> PickSaveFileAsync(SaveFileRequest request)
    {
        if (_topLevel is null)
        {
            return null;
        }

        var file = await _topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = request.Title,
            SuggestedFileName = request.SuggestedFileName,
            DefaultExtension = request.Extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(request.FileTypeName) { Patterns = ["*." + request.Extension] }],
        });
        if (file is null)
        {
            return null;
        }

        return (await file.OpenWriteAsync(), file.Name);
    }

    public async Task SetTextAsync(string text)
    {
        if (_topLevel?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    public bool ItemExists(string path) => File.Exists(path) || Directory.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void Open(string path) => Start(new ProcessStartInfo(path) { UseShellExecute = true });

    // Explorer parses /select itself, so the classic quoted form is required. Windows paths can't contain quotes.
    public void RevealInExplorer(string path) =>
        Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });

    public void Notify(string title, string message, NoticeKind kind = NoticeKind.Information) =>
        Post(() => _notifications?.Show(new Notification(title, message, kind switch
        {
            NoticeKind.Success => NotificationType.Success,
            NoticeKind.Warning => NotificationType.Warning,
            NoticeKind.Error => NotificationType.Error,
            _ => NotificationType.Information,
        }, TimeSpan.FromSeconds(kind is NoticeKind.Warning or NoticeKind.Error ? 12 : 5))));

    private void Start(ProcessStartInfo startInfo)
    {
        try
        {
            using var process = Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            log.Warning("Shell", "Windows couldn't open the item.", exception);
            Notify("Couldn't open", "Windows couldn't open this item. It may have been moved, or no app is set to open it.", NoticeKind.Warning);
        }
    }
}
