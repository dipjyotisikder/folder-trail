using Microsoft.Data.Sqlite;

namespace FolderTrail.Core.SharedKernel.Persistence;

public sealed record DatabaseOptions(string DatabasePath, bool Pooling = true);

/// <summary>
/// Owns the SQLite file. Writes are serialized through a single gate (one writer at a time, each in its own
/// transaction) and all work runs on the thread pool, so callers on the UI thread never block on disk I/O.
/// WAL mode lets readers run concurrently with the writer. Busy/locked errors are retried with backoff.
/// </summary>
public sealed class AppDatabase
{
    private const int MaxAttempts = 6;

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _connectionString;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<Migration> _migrations;

    public AppDatabase(DatabaseOptions options, TimeProvider time)
        : this(options, time, SchemaMigrations.All)
    {
    }

    internal AppDatabase(DatabaseOptions options, TimeProvider time, IReadOnlyList<Migration> migrations)
    {
        DatabasePath = options.DatabasePath;
        _time = time;
        _migrations = migrations;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = options.Pooling,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string DatabasePath { get; }

    public int SchemaVersion { get; private set; }

    public void Initialize()
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = OpenConnection();
        connection.Execute("PRAGMA journal_mode = WAL;");
        DatabaseMigrator.Migrate(connection, _migrations, _time);
        SchemaVersion = DatabaseMigrator.GetUserVersion(connection);
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.Execute("PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA synchronous = NORMAL;");
        return connection;
    }

    public Task<T> ReadAsync<T>(Func<SqliteConnection, T> query, CancellationToken cancellationToken = default) =>
        Task.Run(
            () => WithRetry(
                () =>
                {
                    using var connection = OpenConnection();
                    return query(connection);
                },
                cancellationToken),
            cancellationToken);

    public async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> work, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                    () => WithRetry(
                        () =>
                        {
                            using var connection = OpenConnection();
                            using var transaction = connection.BeginTransaction();
                            var result = work(connection, transaction);
                            transaction.Commit();
                            return result;
                        },
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task WriteAsync(Action<SqliteConnection, SqliteTransaction> work, CancellationToken cancellationToken = default) =>
        WriteAsync(
            (connection, transaction) =>
            {
                work(connection, transaction);
                return true;
            },
            cancellationToken);

    /// <summary>Reclaims file space. Runs outside a transaction, still serialized with other writers.</summary>
    public async Task VacuumAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(
                    () => WithRetry(
                        () =>
                        {
                            using var connection = OpenConnection();
                            connection.Execute("PRAGMA wal_checkpoint(TRUNCATE); VACUUM;");
                            return true;
                        },
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public long GetStorageSizeBytes()
    {
        long total = 0;
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = new FileInfo(DatabasePath + suffix);
            if (file.Exists)
            {
                total += file.Length;
            }
        }

        return total;
    }

    internal static bool IsTransient(SqliteException exception) =>
        exception.SqliteErrorCode is 5 /* SQLITE_BUSY */ or 6 /* SQLITE_LOCKED */;

    private static T WithRetry<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return operation();
            }
            catch (SqliteException exception) when (IsTransient(exception) && attempt < MaxAttempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(40 * attempt * attempt));
            }
        }
    }
}
