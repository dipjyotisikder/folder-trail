using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FolderTrail.Core.SharedKernel.Diagnostics;

public enum DiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record DiagnosticEntry(DateTimeOffset TimestampUtc, DiagnosticSeverity Severity, string Area, string Message)
{
    public string Render(bool includePaths) => includePaths ? Message : PathRedactor.Redact(Message);
}

/// <summary>
/// Paths and file names can be sensitive, so anything persisted to the log file is redacted. Unredacted text only
/// lives in memory and leaves the app only through an explicit, opt-in diagnostic export.
/// </summary>
public static partial class PathRedactor
{
    public const string Placeholder = "[path]";

    public static string Redact(string text)
    {
        var quotedRemoved = QuotedPath().Replace(text, match => match.Groups["q"].Value + Placeholder + match.Groups["q"].Value);
        return UnquotedPath().Replace(quotedRemoved, Placeholder);
    }

    [GeneratedRegex(@"(?<q>['""])(?:[A-Za-z]:[\\/]|\\\\)[^'""\r\n]*\k<q>")]
    private static partial Regex QuotedPath();

    // Unquoted paths may contain spaces, so redaction runs to the end of the line: over-redacting is the safe failure.
    [GeneratedRegex(@"(?:(?<![A-Za-z])[A-Za-z]:[\\/]|\\\\)[^'""\r\n]*")]
    private static partial Regex UnquotedPath();
}

public sealed class DiagnosticLog
{
    private const int Capacity = 300;
    private const int RetainedLogFiles = 7;

    private readonly object _gate = new();
    private readonly LinkedList<DiagnosticEntry> _entries = new();
    private readonly TimeProvider _time;
    private readonly string? _logDirectory;

    public DiagnosticLog(TimeProvider time, string? logDirectory)
    {
        _time = time;
        _logDirectory = logDirectory;
        TrimOldLogFiles();
    }

    public event EventHandler<DiagnosticEntry>? EntryRecorded;

    public string? LogDirectory => _logDirectory;

    public void Info(string area, string message) => Record(DiagnosticSeverity.Information, area, message, null);

    public void Warning(string area, string message, Exception? exception = null) =>
        Record(DiagnosticSeverity.Warning, area, message, exception);

    public void Error(string area, string message, Exception? exception = null) =>
        Record(DiagnosticSeverity.Error, area, message, exception);

    public IReadOnlyList<DiagnosticEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.Reverse().ToList();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    public void DeleteLogFiles()
    {
        if (_logDirectory is null || !Directory.Exists(_logDirectory))
        {
            return;
        }

        lock (_gate)
        {
            foreach (var file in Directory.EnumerateFiles(_logDirectory, "foldertrail-*.log"))
            {
                TryDelete(file);
            }
        }
    }

    private void Record(DiagnosticSeverity severity, string area, string message, Exception? exception)
    {
        var text = exception is null ? message : $"{message} ({exception.GetType().Name}: {exception.Message})";
        var entry = new DiagnosticEntry(_time.GetUtcNow(), severity, area, text);
        lock (_gate)
        {
            _entries.AddLast(entry);
            while (_entries.Count > Capacity)
            {
                _entries.RemoveFirst();
            }

            AppendToFile(entry);
        }

        EntryRecorded?.Invoke(this, entry);
    }

    private void AppendToFile(DiagnosticEntry entry)
    {
        if (_logDirectory is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_logDirectory);
            var file = Path.Combine(
                _logDirectory,
                string.Create(CultureInfo.InvariantCulture, $"foldertrail-{entry.TimestampUtc:yyyyMMdd}.log"));
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{entry.TimestampUtc:yyyy-MM-ddTHH:mm:ss.fffZ} {entry.Severity,-11} {entry.Area}: {entry.Render(includePaths: false)}{Environment.NewLine}");
            File.AppendAllText(file, line, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must never take the app down.
        }
    }

    private void TrimOldLogFiles()
    {
        if (_logDirectory is null || !Directory.Exists(_logDirectory))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(_logDirectory, "foldertrail-*.log")
                         .OrderByDescending(f => f, StringComparer.Ordinal)
                         .Skip(RetainedLogFiles))
            {
                TryDelete(file);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
