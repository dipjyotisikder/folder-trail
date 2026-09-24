using System.Runtime.CompilerServices;
using FolderTrail.Core.SharedKernel.Paths;
using FolderTrail.Core.SharedKernel.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace FolderTrail.Tests.Support;

/// <summary>A test that touches the real file system with Windows path semantics.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires Windows file system semantics.";
        }
    }
}

public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        var raw = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foldertrail-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(raw);
        Path = OperatingSystem.IsWindows() ? WindowsPath.Canonicalize(raw) : raw;
    }

    public string Path { get; }

    public string Combine(string relative) => System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public string CreateFile(string relative, string content = "content", DateTime? lastWriteUtc = null)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        if (lastWriteUtc is { } time)
        {
            File.SetLastWriteTimeUtc(full, time);
        }

        return full;
    }

    public string CreateDirectory(string relative)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                foreach (var directory in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories))
                {
                    var info = new DirectoryInfo(directory);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        info.Delete();
                    }
                }

                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public sealed class TestDatabase : IDisposable
{
    private readonly TempDirectory _directory = new();

    public TestDatabase(FakeTimeProvider? time = null, IReadOnlyList<Migration>? migrations = null, bool initialize = true)
    {
        Time = time ?? new FakeTimeProvider(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
        Path = System.IO.Path.Combine(_directory.Path, "test.db");
        Database = new AppDatabase(new DatabaseOptions(Path, Pooling: false), Time, migrations ?? SchemaMigrations.All);
        if (initialize)
        {
            Database.Initialize();
        }
    }

    public FakeTimeProvider Time { get; }

    public string Path { get; }

    public AppDatabase Database { get; }

    public void Dispose() => _directory.Dispose();
}

public static class Eventually
{
    public static async Task TrueAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.Fail("Timed out waiting until " + because);
    }

    public static async Task TrueAsync(Func<Task<bool>> condition, string because, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.Fail("Timed out waiting until " + because);
    }
}
