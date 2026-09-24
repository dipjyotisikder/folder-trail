using FolderTrail.Core.SharedKernel.Diagnostics;
using FolderTrail.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace FolderTrail.Tests.SharedKernel;

public sealed class DiagnosticLogTests
{
    [Theory]
    [InlineData(@"Access to the path 'C:\Users\Ana\Secret Plans\q3.xlsx' is denied.", "Access to the path '[path]' is denied.")]
    [InlineData("Could not find \"D:\\Payroll\\2026\".", "Could not find \"[path]\".")]
    [InlineData(@"Share \\fileserver\hr\reviews is offline", "Share [path]")]
    [InlineData(@"Failed at C:\Temp\x.txt", "Failed at [path]")]
    [InlineData("C:/forward/slash.txt missing", "[path]")]
    [InlineData("No paths in this message.", "No paths in this message.")]
    public void Redaction_removes_windows_and_unc_paths(string input, string expected) => Assert.Equal(expected, PathRedactor.Redact(input));

    [Fact]
    public void Log_file_never_contains_paths_but_in_memory_entries_can_be_rendered_with_them()
    {
        using var directory = new TempDirectory();
        var log = new DiagnosticLog(new FakeTimeProvider(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)), directory.Path);

        log.Error("Scan", @"Scan of 'C:\Users\Ana\Private' failed.", new UnauthorizedAccessException(@"Access to the path 'C:\Users\Ana\Private\diary.txt' is denied."));

        var file = Directory.GetFiles(directory.Path, "foldertrail-*.log").Single();
        var text = File.ReadAllText(file);
        Assert.DoesNotContain("Ana", text, StringComparison.Ordinal);
        Assert.DoesNotContain("diary", text, StringComparison.Ordinal);
        Assert.Contains("[path]", text, StringComparison.Ordinal);

        var entry = Assert.Single(log.Snapshot());
        Assert.Contains("diary.txt", entry.Render(includePaths: true), StringComparison.Ordinal);
        Assert.DoesNotContain("diary.txt", entry.Render(includePaths: false), StringComparison.Ordinal);
    }

    [Fact]
    public void Ring_buffer_keeps_the_most_recent_entries_newest_first()
    {
        var log = new DiagnosticLog(new FakeTimeProvider(), logDirectory: null);
        for (var i = 0; i < 400; i++)
        {
            log.Info("Test", $"entry {i}");
        }

        var entries = log.Snapshot();
        Assert.Equal(300, entries.Count);
        Assert.Equal("entry 399", entries[0].Message);
    }
}
