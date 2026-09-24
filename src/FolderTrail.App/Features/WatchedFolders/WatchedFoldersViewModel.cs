using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderTrail.App.Shared;
using FolderTrail.App.Shared.Dialogs;
using FolderTrail.App.Shared.Platform;
using FolderTrail.Core.Features.Monitoring;
using FolderTrail.Core.Features.Timeline;
using FolderTrail.Core.Features.WatchedFolders;
using FolderTrail.Core.SharedKernel.Diagnostics;

namespace FolderTrail.App.Features.WatchedFolders;

/// <summary>The sidebar: watched folders with live status, and every folder-management action.</summary>
public sealed partial class WatchedFoldersViewModel : ViewModelBase, IWatchedFolderActions, IDisposable
{
    private readonly WatchedFolderService _folders;
    private readonly MonitoringCoordinator _monitoring;
    private readonly MonitoringGapStore _gaps;
    private readonly TimelineService _timeline;
    private readonly IDialogService _dialogs;
    private readonly IFolderPicker _picker;
    private readonly IShellLauncher _shell;
    private readonly IUiDispatcher _ui;
    private readonly INotifier _notifier;
    private readonly TimeProvider _time;
    private readonly DiagnosticLog _log;
    private ITimer? _clock;
    private long? _selectWhenAttached;

    public WatchedFoldersViewModel(
        WatchedFolderService folders,
        MonitoringCoordinator monitoring,
        MonitoringGapStore gaps,
        TimelineService timeline,
        IDialogService dialogs,
        IFolderPicker picker,
        IShellLauncher shell,
        IUiDispatcher ui,
        INotifier notifier,
        TimeProvider time,
        DiagnosticLog log)
    {
        _folders = folders;
        _monitoring = monitoring;
        _gaps = gaps;
        _timeline = timeline;
        _dialogs = dialogs;
        _picker = picker;
        _shell = shell;
        _ui = ui;
        _notifier = notifier;
        _time = time;
        _log = log;
    }

    public event EventHandler<long?>? SelectionChanged;

    public ObservableCollection<WatchedFolderItemViewModel> Folders { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllActivitySelected))]
    public partial WatchedFolderItemViewModel? SelectedFolder { get; set; }

    public bool IsAllActivitySelected => SelectedFolder is null;

    public bool HasFolders => Folders.Count > 0;

    public void Initialize()
    {
        _monitoring.StatusChanged += OnStatusChanged;
        _monitoring.RootDetached += OnRootDetached;
        foreach (var status in _monitoring.GetStatuses())
        {
            Upsert(status);
        }

        _clock = _time.CreateTimer(_ => _ui.Post(RefreshRelativeTimes), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public void Dispose()
    {
        _monitoring.StatusChanged -= OnStatusChanged;
        _monitoring.RootDetached -= OnRootDetached;
        _clock?.Dispose();
    }

    /// <summary>Forgets every folder, for example after all data was deleted.</summary>
    public void Reset()
    {
        SelectedFolder = null;
        Folders.Clear();
        OnPropertyChanged(nameof(HasFolders));
    }

    public void SelectRoot(long? rootId)
    {
        var target = rootId is null ? null : Folders.FirstOrDefault(f => f.RootId == rootId);
        if (!ReferenceEquals(target, SelectedFolder))
        {
            SelectedFolder = target;
        }
    }

    [RelayCommand]
    private async Task AddFolder() => await AddFolderInteractiveAsync();

    /// <returns>True when a folder was added.</returns>
    public async Task<bool> AddFolderInteractiveAsync()
    {
        var path = await _picker.PickFolderAsync("Choose a folder for FolderTrail to watch");
        return path is not null && await AddFolderFromPathAsync(path);
    }

    public async Task<bool> AddFolderFromPathAsync(string path)
    {
        var result = await _dialogs.ShowAsync<AddFolderResult>(new AddFolderDialogViewModel(path, _folders, _log));
        if (result is not { Succeeded: true, Root: { } root })
        {
            return false;
        }

        _selectWhenAttached = root.Id;
        SelectRoot(root.Id);
        var merged = result.MergedRoots.Count == 0 ? string.Empty : $" It replaces {Format.Count(result.MergedRoots.Count, "folder watch", "folder watches")} inside it; their history is kept.";
        _notifier.Notify("Folder added", $"FolderTrail is indexing \"{root.Name}\" to create a baseline. Changes are recorded from now on.{merged}", NoticeKind.Success);
        return true;
    }

    [RelayCommand]
    private void ShowAllActivity() => SelectedFolder = null;

    [RelayCommand]
    private void ScanAll() => _monitoring.RequestScan();

    [RelayCommand]
    private Task PauseAll() => Guard(() => _folders.PauseAllAsync(), "Couldn't pause monitoring");

    [RelayCommand]
    private Task ResumeAll() => Guard(() => _folders.ResumeAllAsync(), "Couldn't resume monitoring");

    public void ScanNow(WatchedFolderItemViewModel folder) => _monitoring.RequestScan(folder.RootId);

    public Task PauseAsync(WatchedFolderItemViewModel folder) => Guard(() => _folders.PauseAsync(folder.RootId), "Couldn't pause monitoring");

    public Task ResumeAsync(WatchedFolderItemViewModel folder) => Guard(() => _folders.ResumeAsync(folder.RootId), "Couldn't resume monitoring");

    public void Retry(WatchedFolderItemViewModel folder) => _monitoring.Retry(folder.RootId);

    public Task EditExclusionsAsync(WatchedFolderItemViewModel folder) => Guard(
        async () =>
        {
            var rules = await _folders.GetExclusionsAsync(folder.RootId);
            var global = await _folders.GetExclusionsAsync(null);
            var dialog = new ExclusionsDialogViewModel(
                $"Exclusions for {folder.Name}",
                "Changes to excluded files and folders aren't recorded in this folder. Excluding a folder excludes everything inside it.",
                rules,
                isGlobal: false,
                inheritedPatterns: global.Where(r => r.IsEnabled).Select(r => r.Pattern).ToList());
            var edit = await _dialogs.ShowAsync<ExclusionEdit>(dialog);
            if (edit is not null)
            {
                await _folders.SetExclusionsAsync(folder.RootId, edit.Rules);
                _notifier.Notify("Exclusions saved", $"\"{folder.Name}\" is being rescanned so the inventory matches the new exclusions. Items that stop being excluded are indexed without adding activity.", NoticeKind.Success);
            }
        },
        "Couldn't save exclusions");

    public Task RemoveAsync(WatchedFolderItemViewModel folder) => Guard(
        async () =>
        {
            var history = await _timeline.CountAsync(new TimelineFilter(RootIds: [folder.RootId]));
            var choice = await _dialogs.ShowAsync<RemovalChoice>(new RemoveFolderDialogViewModel(folder.Name, folder.DisplayPath, history));
            if (choice is null)
            {
                return;
            }

            await _folders.RemoveAsync(folder.RootId, choice.Mode);
            _notifier.Notify(
                "Folder removed",
                choice.Mode == RemovalMode.DeleteHistory
                    ? $"FolderTrail stopped watching \"{folder.Name}\" and deleted its history."
                    : $"FolderTrail stopped watching \"{folder.Name}\". Its history is still searchable.",
                NoticeKind.Success);
        },
        "Couldn't remove the folder");

    public void OpenInExplorer(WatchedFolderItemViewModel folder)
    {
        if (_shell.DirectoryExists(folder.DisplayPath))
        {
            _shell.Open(folder.DisplayPath);
        }
        else
        {
            _notifier.Notify("Folder not available", "This folder can't be found right now, so it can't be opened.", NoticeKind.Warning);
        }
    }

    partial void OnSelectedFolderChanged(WatchedFolderItemViewModel? value)
    {
        SelectionChanged?.Invoke(this, value?.RootId);
        if (value is not null)
        {
            _ = LoadGapsAsync(value);
        }
    }

    private void OnStatusChanged(object? sender, RootRuntimeStatus status) => _ui.Post(() => Upsert(status));

    private void OnRootDetached(object? sender, long rootId) => _ui.Post(() => RemoveItem(rootId));

    private void Upsert(RootRuntimeStatus status)
    {
        var existing = Folders.FirstOrDefault(f => f.RootId == status.RootId);
        if (existing is not null)
        {
            var gapsChanged = existing.OpenGapCount != status.OpenGapCount || existing.State != status.State;
            existing.Apply(status);
            if (gapsChanged && ReferenceEquals(existing, SelectedFolder))
            {
                _ = LoadGapsAsync(existing);
            }

            return;
        }

        var item = new WatchedFolderItemViewModel(status, this, _time);
        var index = 0;
        while (index < Folders.Count && string.Compare(Folders[index].Name, item.Name, StringComparison.CurrentCultureIgnoreCase) <= 0)
        {
            index++;
        }

        Folders.Insert(index, item);
        OnPropertyChanged(nameof(HasFolders));
        if (_selectWhenAttached == status.RootId)
        {
            _selectWhenAttached = null;
            SelectedFolder = item;
        }
    }

    private void RemoveItem(long rootId)
    {
        var item = Folders.FirstOrDefault(f => f.RootId == rootId);
        if (item is null)
        {
            return;
        }

        if (ReferenceEquals(item, SelectedFolder))
        {
            SelectedFolder = null;
        }

        Folders.Remove(item);
        OnPropertyChanged(nameof(HasFolders));
    }

    private async Task LoadGapsAsync(WatchedFolderItemViewModel folder)
    {
        try
        {
            var gaps = await _gaps.GetRecentAsync(folder.RootId, 10);
            folder.SetGaps(gaps.Select(g => new GapItemViewModel(g, _time.LocalTimeZone)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Warning("UI", "Couldn't load monitoring gaps.", exception);
        }
    }

    private void RefreshRelativeTimes()
    {
        foreach (var folder in Folders)
        {
            folder.RefreshRelativeTimes();
        }
    }

    private async Task Guard(Func<Task> action, string failureTitle)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Error("UI", failureTitle + ".", exception);
            _notifier.Notify(failureTitle, exception.Message, NoticeKind.Error);
        }
    }
}
