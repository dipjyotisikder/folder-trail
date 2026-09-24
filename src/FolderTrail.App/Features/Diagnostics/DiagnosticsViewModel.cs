using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderTrail.App.Shared;
using FolderTrail.App.Shared.Dialogs;
using FolderTrail.App.Shared.Platform;
using FolderTrail.Core.Features.Diagnostics;
using FolderTrail.Core.Features.Monitoring;
using FolderTrail.Core.SharedKernel.Diagnostics;

namespace FolderTrail.App.Features.Diagnostics;

public sealed partial class DiagnosticsViewModel : ViewModelBase
{
    private readonly DiagnosticsService _diagnostics;
    private readonly MonitoringCoordinator _monitoring;
    private readonly IDialogService _dialogs;
    private readonly IFileSavePicker _savePicker;
    private readonly IClipboardService _clipboard;
    private readonly INotifier _notifier;
    private readonly TimeProvider _time;
    private readonly DiagnosticLog _log;

    public DiagnosticsViewModel(
        DiagnosticsService diagnostics,
        MonitoringCoordinator monitoring,
        IDialogService dialogs,
        IFileSavePicker savePicker,
        IClipboardService clipboard,
        INotifier notifier,
        TimeProvider time,
        DiagnosticLog log)
    {
        _diagnostics = diagnostics;
        _monitoring = monitoring;
        _dialogs = dialogs;
        _savePicker = savePicker;
        _clipboard = clipboard;
        _notifier = notifier;
        _time = time;
        _log = log;
    }

    public string CompletenessStatement => DiagnosticsService.CompletenessStatement;

    [ObservableProperty]
    public partial DiagnosticsSnapshot? Snapshot { get; private set; }

    [ObservableProperty]
    public partial bool ShowPaths { get; set; }

    [ObservableProperty]
    public partial bool IsRefreshing { get; private set; }

    public ObservableCollection<FolderDiagnosticsRow> Folders { get; } = new();

    public ObservableCollection<DiagnosticEntryRow> Entries { get; } = new();

    public bool HasFolders => Folders.Count > 0;

    public bool HasEntries => Entries.Count > 0;

    public string AppVersion => Snapshot?.AppVersion ?? DiagnosticsService.AppVersion;

    public string Runtime => Snapshot?.RuntimeVersion ?? string.Empty;

    public string OperatingSystem => Snapshot?.OperatingSystem ?? string.Empty;

    public string DataDirectory => Snapshot?.DataDirectory ?? string.Empty;

    public string DatabasePath => Snapshot?.DatabasePath ?? string.Empty;

    public string DatabaseSize => Snapshot is null ? string.Empty : Format.ShortSize(Snapshot.DatabaseSizeBytes);

    public string SchemaVersion => Snapshot?.SchemaVersion.ToString(CultureInfo.CurrentCulture) ?? string.Empty;

    public string Records => Snapshot is null
        ? string.Empty
        : $"{Format.Count(Snapshot.ActivityRecordCount, "activity record", "activity records")}, {Format.Count(Snapshot.InventoryItemCount, "inventory item", "inventory items")}, {Format.Count(Snapshot.OpenGapCount, "open monitoring gap", "open monitoring gaps")}";

    public string CapturedText => Snapshot is null ? string.Empty : "Updated " + Format.LocalTime(Snapshot.CapturedUtc, _time.LocalTimeZone);

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            Snapshot = await _diagnostics.CaptureAsync();
            Folders.Clear();
            foreach (var folder in Snapshot.Folders)
            {
                Folders.Add(new FolderDiagnosticsRow(folder, _time));
            }

            RenderEntries();
            OnPropertyChanged(string.Empty);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Error("UI", "Collecting diagnostics failed.", exception);
            _notifier.Notify("Couldn't collect diagnostics", exception.Message, NoticeKind.Error);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private void ScanAll()
    {
        _monitoring.RequestScan();
        _notifier.Notify("Scan requested", "Every watched folder that is available will be scanned.", NoticeKind.Information);
    }

    [RelayCommand]
    private void RetryProblems()
    {
        var problems = _monitoring.GetStatuses().Where(s => s.State is RootState.Missing or RootState.NeedsAttention).ToList();
        foreach (var status in problems)
        {
            _monitoring.Retry(status.RootId);
        }

        _notifier.Notify("Retrying", problems.Count == 0 ? "No folders currently need attention." : $"Checking {Format.Count(problems.Count, "folder", "folders")} again.", NoticeKind.Information);
    }

    [RelayCommand]
    private async Task Export()
    {
        var choice = await _dialogs.ShowAsync<DiagnosticsExportChoice>(new ExportDiagnosticsDialogViewModel());
        if (choice is null)
        {
            return;
        }

        try
        {
            var snapshot = await _diagnostics.CaptureAsync();
            var target = await _savePicker.PickSaveFileAsync(new SaveFileRequest(
                "Export diagnostics",
                string.Create(CultureInfo.InvariantCulture, $"FolderTrail diagnostics {Format.ToLocal(_time.GetUtcNow(), _time.LocalTimeZone):yyyy-MM-dd HHmm}.txt"),
                "txt",
                "Text file"));
            if (target is not { } file)
            {
                return;
            }

            await using (file.Stream)
            await using (var writer = new StreamWriter(file.Stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                await writer.WriteAsync(DiagnosticsService.BuildReport(snapshot, choice.IncludePaths));
            }

            _notifier.Notify("Diagnostics exported", $"Saved to {file.FileName}{(choice.IncludePaths ? " (includes paths)." : " (paths removed).")}", NoticeKind.Success);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Error("UI", "Exporting diagnostics failed.", exception);
            _notifier.Notify("Export failed", exception.Message, NoticeKind.Error);
        }
    }

    [RelayCommand]
    private async Task CopyReport()
    {
        if (Snapshot is null)
        {
            await RefreshAsync();
        }

        if (Snapshot is not null)
        {
            await _clipboard.SetTextAsync(DiagnosticsService.BuildReport(Snapshot, includePaths: false));
            _notifier.Notify("Copied", "A diagnostics summary without paths was copied to the clipboard.", NoticeKind.Success);
        }
    }

    partial void OnShowPathsChanged(bool value) => RenderEntries();

    private void RenderEntries()
    {
        Entries.Clear();
        foreach (var entry in Snapshot?.RecentEntries.Where(e => e.Severity != DiagnosticSeverity.Information).Take(50) ?? [])
        {
            Entries.Add(new DiagnosticEntryRow(entry, ShowPaths, _time.LocalTimeZone));
        }

        OnPropertyChanged(nameof(HasEntries));
    }
}

public sealed class FolderDiagnosticsRow(RootRuntimeStatus status, TimeProvider time)
{
    public string Name => status.Name;

    public string Path => status.DisplayPath;

    public string State => RootRuntimeStatus.Describe(status.State);

    public string Watcher => status.IsLiveWatcherActive ? "Receiving notifications" : "Not running";

    public bool IsWatcherActive => status.IsLiveWatcherActive;

    public string LastScan => Format.Relative(status.LastSuccessfulScanUtc, time.GetUtcNow(), time.LocalTimeZone);

    public string LastEvent => Format.Relative(status.LastEventUtc, time.GetUtcNow(), time.LocalTimeZone, never: "None yet");

    public string Skipped => status.LastScanSkippedCount.ToString("N0", CultureInfo.CurrentCulture);

    public string Gaps => status.OpenGapCount.ToString("N0", CultureInfo.CurrentCulture);

    public string History => status.OpenGapCount > 0
        ? "Not verifiable: there are open monitoring gaps"
        : status.LastSuccessfulScanUtc is null
            ? "Not verifiable yet: no completed scan"
            : "Not verifiable: live notifications can be lost";
}

public sealed class DiagnosticEntryRow(DiagnosticEntry entry, bool showPaths, TimeZoneInfo zone)
{
    public string Time => Format.LocalDateTime(entry.TimestampUtc, zone);

    public string Severity => entry.Severity.ToString();

    public bool IsError => entry.Severity == DiagnosticSeverity.Error;

    public string Area => entry.Area;

    public string Message => entry.Render(showPaths);
}

public sealed record DiagnosticsExportChoice(bool IncludePaths);

public sealed partial class ExportDiagnosticsDialogViewModel : DialogViewModel
{
    public override string Title => "Export diagnostics";

    [ObservableProperty]
    public partial bool IncludePaths { get; set; }

    [RelayCommand]
    private void Confirm() => Close(new DiagnosticsExportChoice(IncludePaths));
}
