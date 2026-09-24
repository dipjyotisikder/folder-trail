using System.Text;
using FolderTrail.Core.Features.Timeline;
using FolderTrail.Core.SharedKernel.Journal;

namespace FolderTrail.Tests.Features.Timeline;

public sealed class CsvExporterTests
{
    private static readonly TimeZoneInfo Dhaka = TimeZoneInfo.CreateCustomTimeZone("Test+06", TimeSpan.FromHours(6), "Test+06", "Test+06");

    private static TimelineEntry Entry(string path, string? oldPath = null, ActivityType type = ActivityType.Modified) =>
        new(7, 1, @"C:\Watched", false, new DateTimeOffset(2026, 3, 4, 22, 30, 15, 123, TimeSpan.Zero), type, ActivitySource.Live, ItemKind.File,
            path, FolderTrail.Core.SharedKernel.Paths.WindowsPath.GetName(path), oldPath, 1_234, null, 1_000, null, null, 2);

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with,comma", "\"with,comma\"")]
    [InlineData("with \"quotes\"", "\"with \"\"quotes\"\"\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    [InlineData("carriage\r\nreturn", "\"carriage\r\nreturn\"")]
    [InlineData(" padded ", "\" padded \"")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Fields_are_escaped_per_rfc_4180(string? value, string expected) => Assert.Equal(expected, CsvExporter.Escape(value));

    [Theory]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@x", "'@x")]
    [InlineData("=a,b", "\"'=a,b\"")]
    public void Values_a_spreadsheet_would_evaluate_are_neutralized(string value, string expected) =>
        Assert.Equal(expected, CsvExporter.Escape(value));

    [Fact]
    public void Times_are_written_as_utc_iso_8601_and_local_with_offset()
    {
        var fields = CsvExporter.ToFields(Entry(@"C:\Watched\a.txt"), Dhaka);
        Assert.Equal("2026-03-04T22:30:15.123Z", fields[0]);
        Assert.Equal("2026-03-05 04:30:15 +06:00", fields[1]);
    }

    [Fact]
    public async Task Export_is_utf8_with_bom_crlf_and_round_trips_unicode_paths()
    {
        using var stream = new MemoryStream();
        var entries = new[]
        {
            Entry(@"C:\Watched\Проект\日本語 ファイル, ""final"".txt"),
            Entry(@"C:\Watched\Ünïcödé\new.txt", oldPath: @"C:\Watched\Ünïcödé\old.txt", type: ActivityType.Renamed),
        };

        await using (var writer = CsvExporter.CreateWriter(stream))
        {
            var count = await CsvExporter.WriteAsync(writer, entries.ToAsyncEnumerable(), Dhaka, TestContext.Current.CancellationToken);
            Assert.Equal(2, count);
        }

        var bytes = stream.ToArray();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("\"Detected (UTC, ISO 8601)\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"C:\\Watched\\Проект\\日本語 ファイル, \"\"final\"\".txt\"", lines[1], StringComparison.Ordinal);
        Assert.Contains("Renamed", lines[2], StringComparison.Ordinal);
        Assert.Contains(@"C:\Watched\Ünïcödé\old.txt", lines[2], StringComparison.Ordinal);
        Assert.Equal(CsvExporter.Header.Count, ParseCsvLine(lines[2]).Count);
    }

    [Fact]
    public void Header_and_rows_have_the_same_number_of_columns() =>
        Assert.Equal(CsvExporter.Header.Count, CsvExporter.ToFields(Entry(@"C:\Watched\a.txt"), Dhaka).Count);

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
