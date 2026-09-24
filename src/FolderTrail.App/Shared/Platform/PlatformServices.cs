namespace FolderTrail.App.Shared.Platform;

/// <summary>Marshals work onto the UI thread; Core raises its events on background threads.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public interface IFolderPicker
{
    /// <returns>The chosen local folder path, or null when cancelled.</returns>
    Task<string?> PickFolderAsync(string title);
}

public sealed record SaveFileRequest(string Title, string SuggestedFileName, string Extension, string FileTypeName);

public interface IFileSavePicker
{
    /// <returns>A writable stream and the chosen file name, or null when cancelled.</returns>
    Task<(Stream Stream, string FileName)?> PickSaveFileAsync(SaveFileRequest request);
}

public interface IClipboardService
{
    Task SetTextAsync(string text);
}

/// <summary>Operating-system shell integration: opening items and revealing them in File Explorer.</summary>
public interface IShellLauncher
{
    bool ItemExists(string path);

    bool DirectoryExists(string path);

    /// <summary>Opens a file with its default app, or a folder in File Explorer.</summary>
    void Open(string path);

    /// <summary>Opens File Explorer at the item's parent folder with the item selected.</summary>
    void RevealInExplorer(string path);
}

public enum NoticeKind
{
    Information,
    Success,
    Warning,
    Error,
}

/// <summary>In-window notices. Never used per file event, only for actions and monitoring problems.</summary>
public interface INotifier
{
    void Notify(string title, string message, NoticeKind kind = NoticeKind.Information);
}

public interface IMainWindowController
{
    void ShowAndActivate();

    void RequestQuit();
}
