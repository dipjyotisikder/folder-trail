using System.Globalization;
using System.Text;

namespace FolderTrail.Core.Features.Timeline;

/// <summary>
/// RFC 4180 CSV in UTF-8 (with BOM so spreadsheet apps detect the encoding), CRLF line endings. Detection times
/// are written both as UTC ISO 8601 and in local time with its offset. Values that a spreadsheet would run as a
/// formula (starting with = + - @ or a control character) are prefixed with an apostrophe.
/// </summary>
public static class CsvExporter
{
    public static IReadOnlyList<string> Header { get; } =
    [
        "Detected (UTC, ISO 8601)", "Detected (local time)", "Event", "Source", "Item type", "Name", "Folder", "Path",
        "Old path", "Watched folder", "Size (bytes)", "Previous size (bytes)", "Last modified (UTC)", "Notifications merged",
    ];

    public static StreamWriter CreateWriter(Stream destination) =>
        new(destination, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), bufferSize: 64 * 1024, leaveOpen: true)
        {
            NewLine = "\r\n",
        };

    public static async Task<int> WriteAsync(TextWriter writer, IAsyncEnumerable<TimelineEntry> entries, TimeZoneInfo localZone, CancellationToken cancellationToken = default)
    {
        await writer.WriteLineAsync(FormatRow(Header)).ConfigureAwait(false);
        var count = 0;
        await foreach (var entry in entries.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            await writer.WriteLineAsync(FormatRow(ToFields(entry, localZone))).ConfigureAwait(false);
            count++;
        }

        return count;
    }

    public static IReadOnlyList<string?> ToFields(TimelineEntry entry, TimeZoneInfo localZone) =>
    [
        FormatUtc(entry.DetectedUtc),
        FormatLocal(entry.DetectedUtc, localZone),
        ActivityText.Verb(entry.Type),
        ActivityText.Source(entry.Source),
        ActivityText.Kind(entry.Kind),
        entry.Name,
        entry.ParentFolder,
        entry.Path,
        entry.OldPath,
        entry.RootPath,
        entry.Length?.ToString(CultureInfo.InvariantCulture),
        entry.PreviousLength?.ToString(CultureInfo.InvariantCulture),
        entry.LastWriteUtc is { } lastWrite ? FormatUtc(lastWrite) : null,
        entry.MergedCount.ToString(CultureInfo.InvariantCulture),
    ];

    public static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static string FormatLocal(DateTimeOffset value, TimeZoneInfo localZone)
    {
        var local = TimeZoneInfo.ConvertTime(value, localZone);
        return local.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
    }

    public static string FormatRow(IEnumerable<string?> fields) => string.Join(',', fields.Select(Escape));

    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        var needsQuotes = value.AsSpan().IndexOfAny(",\"\r\n") >= 0 || value[0] == ' ' || value[^1] == ' ';
        return needsQuotes ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
