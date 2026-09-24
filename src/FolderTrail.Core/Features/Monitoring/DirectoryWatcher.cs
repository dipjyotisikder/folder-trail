using FolderTrail.Core.Features.WatchedFolders;

namespace FolderTrail.Core.Features.Monitoring;

public enum RawEventKind
{
    Created,
    Changed,
    Deleted,
    Renamed,
}

/// <param name="OldFullPath">The previous path of a rename, when the platform supplies it.</param>
public readonly record struct RawFileEvent(long RootId, RawEventKind Kind, string FullPath, string? OldFullPath, DateTimeOffset ObservedUtc);

public sealed record WatcherFailure(long RootId, bool IsBufferOverflow, Exception Exception);

public interface IDirectoryWatcher : IDisposable
{
    void Start();
}

public interface IDirectoryWatcherFactory
{
    /// <remarks>Both callbacks run on the watcher's own thread and must only hand the event off.</remarks>
    IDirectoryWatcher Create(WatchedRoot root, Action<RawFileEvent> onEvent, Action<WatcherFailure> onFailure);
}

public sealed class FileSystemWatcherFactory(TimeProvider time) : IDirectoryWatcherFactory
{
    public IDirectoryWatcher Create(WatchedRoot root, Action<RawFileEvent> onEvent, Action<WatcherFailure> onFailure) =>
        new FileSystemDirectoryWatcher(root, onEvent, onFailure, time);
}

/// <summary>One FileSystemWatcher per watched folder. It is a hint source: reconciliation covers what it misses.</summary>
internal sealed class FileSystemDirectoryWatcher : IDirectoryWatcher
{
    // 64 KB is the documented maximum that also works for network shares; larger buffers pin non-paged memory.
    private const int BufferSize = 64 * 1024;

    private readonly FileSystemWatcher _watcher;
    private readonly long _rootId;
    private readonly Action<RawFileEvent> _onEvent;
    private readonly Action<WatcherFailure> _onFailure;
    private readonly TimeProvider _time;
    private volatile bool _disposed;

    public FileSystemDirectoryWatcher(WatchedRoot root, Action<RawFileEvent> onEvent, Action<WatcherFailure> onFailure, TimeProvider time)
    {
        _rootId = root.Id;
        _onEvent = onEvent;
        _onFailure = onFailure;
        _time = time;
        _watcher = new FileSystemWatcher(root.DisplayPath)
        {
            IncludeSubdirectories = root.IncludeSubfolders,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = BufferSize,
        };
        _watcher.Created += (_, e) => Raise(RawEventKind.Created, e.FullPath, null);
        _watcher.Changed += (_, e) => Raise(RawEventKind.Changed, e.FullPath, null);
        _watcher.Deleted += (_, e) => Raise(RawEventKind.Deleted, e.FullPath, null);
        _watcher.Renamed += (_, e) => Raise(RawEventKind.Renamed, e.FullPath, string.IsNullOrEmpty(e.OldFullPath) ? null : e.OldFullPath);
        _watcher.Error += OnError;
    }

    public void Start() => _watcher.EnableRaisingEvents = true;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _watcher.EnableRaisingEvents = false;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
        }

        _watcher.Dispose();
    }

    private void Raise(RawEventKind kind, string path, string? oldPath)
    {
        if (!_disposed)
        {
            _onEvent(new RawFileEvent(_rootId, kind, path, oldPath, _time.GetUtcNow()));
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        if (!_disposed)
        {
            var exception = e.GetException();
            _onFailure(new WatcherFailure(_rootId, exception is InternalBufferOverflowException, exception));
        }
    }
}
