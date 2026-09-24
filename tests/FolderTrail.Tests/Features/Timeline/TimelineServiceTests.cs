using FolderTrail.Core.Features.Timeline;
using FolderTrail.Core.SharedKernel.Journal;
using FolderTrail.Core.SharedKernel.Paths;
using FolderTrail.Tests.Support;

namespace FolderTrail.Tests.Features.Timeline;

public sealed class TimelineServiceTests : IAsyncLifetime
{
    private readonly CoreHarness _harness = new(probe: new FakeProbe(), scanner: new FakeScanner());
    private long _docs;
    private long _photos;
    private DateTimeOffset _now;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _docs = (await _harness.AddRootAsync(@"C:\Docs")).Id;
        _photos = (await _harness.AddRootAsync(@"D:\Photos")).Id;
        _now = _harness.Time.GetUtcNow();
        var items = new List<NewActivity>
        {
            new(_docs, _now.AddMinutes(-50), ActivityType.Created, ActivitySource.Live, ItemKind.File, @"C:\Docs\Budget 2026.xlsx"),
            new(_docs, _now.AddMinutes(-40), ActivityType.Modified, ActivitySource.Live, ItemKind.File, @"C:\Docs\Budget 2026.xlsx", Length: 10),
            new(_docs, _now.AddMinutes(-30), ActivityType.Renamed, ActivitySource.Live, ItemKind.File, @"C:\Docs\Final Budget.xlsx", OldPath: @"C:\Docs\Budget 2026.xlsx"),
            new(_docs, _now.AddMinutes(-20), ActivityType.Deleted, ActivitySource.Reconciliation, ItemKind.File, @"C:\Docs\100% done_v2.txt"),
            new(_photos, _now.AddMinutes(-10), ActivityType.Created, ActivitySource.ManualScan, ItemKind.File, @"D:\Photos\Été\Plage.jpg"),
            new(_photos, _now.AddDays(-3), ActivityType.Created, ActivitySource.Live, ItemKind.Directory, @"D:\Photos\Été"),
        };
        await _harness.Database.WriteAsync((c, t) => ActivityJournal.Append(c, t, items), Token);
    }

    public ValueTask DisposeAsync()
    {
        _harness.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Results_are_newest_first()
    {
        var page = await _harness.Timeline.GetPageAsync(TimelineFilter.All, cancellationToken: Token);
        Assert.Equal(page.Items.OrderByDescending(e => e.DetectedUtc).Select(e => e.Id), page.Items.Select(e => e.Id));
        Assert.Null(page.Next);
        Assert.Equal("Plage.jpg", page.Items[0].Name);
    }

    [Fact]
    public async Task Keyset_paging_returns_every_row_exactly_once_even_with_identical_timestamps()
    {
        var same = _now.AddMinutes(-5);
        await _harness.Database.WriteAsync(
            (c, t) => ActivityJournal.Append(c, t, Enumerable.Range(0, 25).Select(i => new NewActivity(_docs, same, ActivityType.Modified, ActivitySource.Live, ItemKind.File, $@"C:\Docs\same{i}.txt")).ToList()),
            Token);

        var seen = new List<long>();
        TimelineCursor? cursor = null;
        do
        {
            var page = await _harness.Timeline.GetPageAsync(TimelineFilter.All, cursor, pageSize: 7, Token);
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.Next;
        }
        while (cursor is not null);

        Assert.Equal(31, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Theory]
    [InlineData("budget", 3)]
    [InlineData("BUDGET 2026", 3)]
    [InlineData("final budget", 1)]
    [InlineData("été", 2)]
    [InlineData("ÉTÉ plage", 1)]
    [InlineData("100%", 1)]
    [InlineData("done_v", 1)]
    [InlineData("_", 1)]
    [InlineData("docs/final", 1)]
    [InlineData("nothing-matches", 0)]
    public async Task Search_matches_names_and_paths_case_insensitively_with_literal_wildcards(string text, int expected)
    {
        var page = await _harness.Timeline.GetPageAsync(new TimelineFilter(SearchText: text), cancellationToken: Token);
        Assert.Equal(expected, page.Items.Count);
    }

    [Fact]
    public async Task Search_also_matches_the_old_path_of_renames()
    {
        var page = await _harness.Timeline.GetPageAsync(new TimelineFilter(SearchText: "Budget 2026", Types: [ActivityType.Renamed]), cancellationToken: Token);
        Assert.Equal("Final Budget.xlsx", Assert.Single(page.Items).Name);
    }

    [Fact]
    public async Task Filters_combine_folder_type_source_and_date_range()
    {
        Assert.Equal(4, (await Page(new TimelineFilter(RootIds: [_docs]))).Count);
        Assert.Equal(3, (await Page(new TimelineFilter(Types: [ActivityType.Created]))).Count);
        Assert.Equal(2, (await Page(new TimelineFilter(Sources: [ActivitySource.Reconciliation, ActivitySource.ManualScan]))).Count);
        Assert.Equal(5, (await Page(new TimelineFilter(FromUtc: _now.AddHours(-1)))).Count);
        Assert.Single(await Page(new TimelineFilter(FromUtc: _now.AddDays(-4), ToUtc: _now.AddDays(-2))));
        Assert.Single(await Page(new TimelineFilter(RootIds: [_photos], Types: [ActivityType.Created], Sources: [ActivitySource.ManualScan])));
        Assert.Equal(2, await _harness.Timeline.CountAsync(new TimelineFilter(RootIds: [_photos]), Token));
    }

    [Fact]
    public async Task Entries_added_later_are_found_even_when_dated_earlier_than_those_shown()
    {
        var shown = (await _harness.Timeline.GetPageAsync(TimelineFilter.All, cancellationToken: Token)).Items;
        await _harness.Database.WriteAsync(
            (c, t) => ActivityJournal.Append(c, t,
            [
                new NewActivity(_docs, _now.AddMinutes(1), ActivityType.Created, ActivitySource.Live, ItemKind.File, @"C:\Docs\later.txt"),
                new NewActivity(_docs, _now.AddHours(-2), ActivityType.Modified, ActivitySource.Live, ItemKind.File, @"C:\Docs\held.txt"),
            ]),
            Token);

        var added = await _harness.Timeline.GetAddedAfterAsync(TimelineFilter.All, shown.Max(e => e.Id), cancellationToken: Token);

        Assert.Equal(["later.txt", "held.txt"], added.Select(e => e.Name));
    }

    [Fact]
    public async Task Entries_expose_parent_folder_and_old_name()
    {
        var rename = Assert.Single(await Page(new TimelineFilter(Types: [ActivityType.Renamed])));
        Assert.Equal(@"C:\Docs", rename.ParentFolder);
        Assert.Equal("Budget 2026.xlsx", rename.OldName);
        Assert.Equal(@"C:\Docs", rename.RootPath);
        Assert.False(rename.IsInferred);
        Assert.True(Assert.Single(await Page(new TimelineFilter(Types: [ActivityType.Deleted]))).IsInferred);
    }

    [Fact]
    public async Task Csv_export_writes_the_filtered_set()
    {
        using var stream = new MemoryStream();
        var count = await _harness.Timeline.ExportCsvAsync(new TimelineFilter(RootIds: [_photos]), stream, TimeZoneInfo.Utc, Token);
        Assert.Equal(2, count);
        var text = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains(@"D:\Photos\Été\Plage.jpg", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Budget", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Like_escaping_uses_a_character_that_cannot_appear_in_windows_names() =>
        Assert.Equal("100|%|_A||B", TimelineService.EscapeLike("100%_A|B"));

    private async Task<IReadOnlyList<TimelineEntry>> Page(TimelineFilter filter) =>
        (await _harness.Timeline.GetPageAsync(filter, cancellationToken: Token)).Items;

    [Fact]
    public async Task Root_options_list_active_folders_first()
    {
        var options = await _harness.Timeline.GetRootOptionsAsync(Token);
        Assert.Equal([WindowsPath.Canonicalize(@"C:\Docs"), WindowsPath.Canonicalize(@"D:\Photos")], options.Select(o => o.DisplayPath));
    }
}
