using CommunityToolkit.Mvvm.ComponentModel;
using FolderTrail.App.Shared;
using FolderTrail.Core.Features.Timeline;
using FolderTrail.Core.SharedKernel.Journal;

namespace FolderTrail.App.Features.Timeline;

public abstract class TimelineRow : ViewModelBase
{
    public abstract bool IsSelectable { get; }

    public abstract string AutomationName { get; }
}

public sealed partial class DayHeaderRow : TimelineRow
{
    public DayHeaderRow(DateOnly day, string title)
    {
        Day = day;
        Title = title;
    }

    public DateOnly Day { get; }

    [ObservableProperty]
    public partial string Title { get; set; }

    public override bool IsSelectable => false;

    public override string AutomationName => Title;
}

public sealed class EventRow : TimelineRow
{
    public EventRow(TimelineEntry entry, TimeZoneInfo zone)
    {
        Entry = entry;
        Day = DateOnly.FromDateTime(Format.ToLocal(entry.DetectedUtc, zone).DateTime);
        TimeText = Format.LocalTime(entry.DetectedUtc, zone);
    }

    public TimelineEntry Entry { get; }

    public DateOnly Day { get; }

    public override bool IsSelectable => true;

    public string Verb => ActivityText.Verb(Entry.Type);

    public string Name => Entry.Name;

    public string Detail => Entry.Type == ActivityType.Renamed && Entry.OldName is { } oldName
        ? $"from {oldName} · {Entry.ParentFolder}"
        : Entry.ParentFolder ?? Entry.Path;

    public string TimeText { get; }

    public string SourceText => Entry.Source switch
    {
        ActivitySource.Live => "Live",
        ActivitySource.ManualScan => "Manual scan",
        _ => "Scan",
    };

    public bool IsInferred => Entry.IsInferred;

    public bool IsCreated => Entry.Type == ActivityType.Created;

    public bool IsModified => Entry.Type == ActivityType.Modified;

    public bool IsDeleted => Entry.Type == ActivityType.Deleted;

    public bool IsRenamed => Entry.Type == ActivityType.Renamed;

    public bool IsFolder => Entry.Kind == ItemKind.Directory;

    public override string AutomationName => $"{Verb} {Name}, in {Entry.ParentFolder}, at {TimeText}, {ActivityText.Source(Entry.Source)}";
}

public enum DateRange
{
    AnyTime,
    Today,
    Last7Days,
    Last30Days,
    Custom,
}

public sealed record RootFilterOption(long? RootId, string Label)
{
    public override string ToString() => Label;
}
