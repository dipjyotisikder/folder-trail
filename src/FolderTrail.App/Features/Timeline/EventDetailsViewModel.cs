using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderTrail.App.Shared;
using FolderTrail.App.Shared.Platform;
using FolderTrail.Core.Features.Timeline;
using FolderTrail.Core.SharedKernel.Journal;

namespace FolderTrail.App.Features.Timeline;

public sealed partial class EventDetailsViewModel(IShellLauncher shell, IClipboardService clipboard, INotifier notifier, TimeProvider time) : ViewModelBase
{
    private int _version;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEntry))]
    public partial TimelineEntry? Entry { get; private set; }

    /// <summary>Null while the check is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailabilityNote), nameof(HasAvailabilityNote))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(ShowInExplorerCommand))]
    public partial bool? ItemExists { get; private set; }

    public bool HasEntry => Entry is not null;

    private TimeZoneInfo Zone => time.LocalTimeZone;

    public string Verb => Entry is null ? string.Empty : ActivityText.Verb(Entry.Type);

    public string Name => Entry?.Name ?? string.Empty;

    public string FullPath => Entry?.Path ?? string.Empty;

    public string? OldPath => Entry?.OldPath;

    public bool HasOldPath => Entry?.OldPath is not null;

    public string WatchedFolder => Entry is null ? string.Empty : Entry.RootRemoved ? Entry.RootPath + " (no longer watched)" : Entry.RootPath;

    public string SourceText => Entry is null ? string.Empty : ActivityText.Source(Entry.Source);

    public string SourceExplanation => Entry?.Source switch
    {
        ActivitySource.Live => "Reported by Windows as it happened.",
        ActivitySource.ManualScan => "Found by a scan you started, by comparing with what FolderTrail last recorded. The change happened some time before the time shown, which is when the scan found it.",
        ActivitySource.Reconciliation => "Found by an automatic scan, by comparing with what FolderTrail last recorded. The change happened some time before the time shown, which is when the scan found it.",
        _ => string.Empty,
    };

    public string DetectedLabel => Entry?.IsInferred == true ? "Detected by scan (local time)" : "Detected (local time)";

    public string DetectedLocal => Entry is null ? string.Empty : Format.LocalDateTime(Entry.DetectedUtc, Zone);

    public string DetectedUtc => Entry is null ? string.Empty : Format.Utc(Entry.DetectedUtc);

    public string KindText => Entry is null ? string.Empty : ActivityText.Kind(Entry.Kind);

    public bool IsFolder => Entry?.Kind == ItemKind.Directory;

    public string SizeText => Entry is null || Entry.Kind == ItemKind.Directory
        ? "Not applicable"
        : Entry.Type == ActivityType.Deleted ? "Not applicable (deleted)" : Format.Size(Entry.Length);

    public bool HasPreviousSize => Entry?.PreviousLength is not null;

    public string PreviousSizeText => Format.Size(Entry?.PreviousLength);

    public bool HasLastWrite => Entry?.LastWriteUtc is not null;

    public string LastWriteText => Entry?.LastWriteUtc is { } value ? Format.LocalDateTime(value, Zone) : string.Empty;

    public bool HasPreviousObserved => Entry?.PreviousObservedUtc is not null;

    public string PreviousObservedText => Entry?.PreviousObservedUtc is { } value ? Format.LocalDateTime(value, Zone) : string.Empty;

    public bool HasPreviousLastWrite => Entry?.PreviousLastWriteUtc is not null;

    public string PreviousLastWriteText => Entry?.PreviousLastWriteUtc is { } value ? Format.LocalDateTime(value, Zone) : string.Empty;

    public bool HasMerged => Entry?.MergedCount > 1;

    public string MergedText => Entry is null ? string.Empty : $"{Entry.MergedCount} change notifications within a short time were combined into this record.";

    public bool IsCreated => Entry?.Type == ActivityType.Created;

    public bool IsModified => Entry?.Type == ActivityType.Modified;

    public bool IsDeleted => Entry?.Type == ActivityType.Deleted;

    public bool IsRenamed => Entry?.Type == ActivityType.Renamed;

    public string OpenText => IsFolder ? "Open folder" : "Open file";

    public string? AvailabilityNote => ItemExists switch
    {
        null when Entry is not null => "Checking whether this item still exists…",
        false => "This item no longer exists at this path, so it can't be opened or shown in File Explorer. FolderTrail doesn't keep copies of files.",
        _ => null,
    };

    public bool HasAvailabilityNote => AvailabilityNote is not null;

    public async Task ShowAsync(TimelineEntry? entry)
    {
        var version = ++_version;
        Entry = entry;
        ItemExists = null;
        OnPropertyChanged(string.Empty);
        if (entry is null)
        {
            return;
        }

        var exists = await Task.Run(() => shell.ItemExists(entry.Path));
        if (version == _version)
        {
            ItemExists = exists;
        }
    }

    private bool CanUseItem() => ItemExists == true;

    [RelayCommand(CanExecute = nameof(CanUseItem))]
    private void Open()
    {
        if (Entry is not null)
        {
            shell.Open(Entry.Path);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseItem))]
    private void ShowInExplorer()
    {
        if (Entry is not null)
        {
            shell.RevealInExplorer(Entry.Path);
        }
    }

    [RelayCommand]
    private async Task CopyPath()
    {
        if (Entry is not null)
        {
            await clipboard.SetTextAsync(Entry.Path);
            notifier.Notify("Copied", "The path was copied to the clipboard.", NoticeKind.Success);
        }
    }

    [RelayCommand]
    private async Task CopyDetails()
    {
        if (Entry is not null)
        {
            await clipboard.SetTextAsync(DescribeForClipboard(Entry, Zone));
            notifier.Notify("Copied", "The event details were copied to the clipboard.", NoticeKind.Success);
        }
    }

    public static string DescribeForClipboard(TimelineEntry entry, TimeZoneInfo zone)
    {
        var builder = new StringBuilder();
        var culture = CultureInfo.CurrentCulture;
        builder.AppendLine(culture, $"Event: {ActivityText.Verb(entry.Type)}");
        builder.AppendLine(culture, $"Item type: {ActivityText.Kind(entry.Kind)}");
        builder.AppendLine(culture, $"Name: {entry.Name}");
        builder.AppendLine(culture, $"Path: {entry.Path}");
        if (entry.OldPath is not null)
        {
            builder.AppendLine(culture, $"Old path: {entry.OldPath}");
        }

        builder.AppendLine(culture, $"Watched folder: {entry.RootPath}");
        builder.AppendLine(culture, $"Source: {ActivityText.Source(entry.Source)}");
        builder.AppendLine(culture, $"Detected (local): {Format.LocalDateTime(entry.DetectedUtc, zone)}");
        builder.AppendLine(culture, $"Detected (UTC): {CsvExporter.FormatUtc(entry.DetectedUtc)}");
        if (entry.Length is not null)
        {
            builder.AppendLine(culture, $"Size: {Format.Size(entry.Length)}");
        }

        if (entry.PreviousLength is not null)
        {
            builder.AppendLine(culture, $"Previous size: {Format.Size(entry.PreviousLength)}");
        }

        return builder.ToString();
    }
}
