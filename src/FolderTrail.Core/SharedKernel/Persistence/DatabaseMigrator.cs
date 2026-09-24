using System.Globalization;
using Microsoft.Data.Sqlite;

namespace FolderTrail.Core.SharedKernel.Persistence;

public sealed record Migration(int Version, string Description, string Sql);

public sealed class UnsupportedSchemaVersionException(int databaseVersion, int supportedVersion)
    : Exception(
        $"The FolderTrail database uses schema version {databaseVersion}, but this version of the app supports up to {supportedVersion}. " +
        "It was probably created by a newer FolderTrail. Install the newer version, or delete the app data to start over.")
{
    public int DatabaseVersion { get; } = databaseVersion;

    public int SupportedVersion { get; } = supportedVersion;
}

/// <summary>
/// Explicit schema versioning: <c>PRAGMA user_version</c> is the authoritative version and every applied
/// migration is also recorded in the <c>schema_version</c> table. Each migration runs in its own transaction.
/// </summary>
public static class DatabaseMigrator
{
    public static int GetUserVersion(SqliteConnection connection)
    {
        using var command = connection.Command("PRAGMA user_version;");
        return (int)command.ScalarInt64();
    }

    public static void Migrate(SqliteConnection connection, IReadOnlyList<Migration> migrations, TimeProvider time, int? targetVersion = null)
    {
        var current = GetUserVersion(connection);
        var latest = migrations.Count == 0 ? 0 : migrations.Max(m => m.Version);
        if (current > latest)
        {
            throw new UnsupportedSchemaVersionException(current, latest);
        }

        foreach (var migration in migrations.OrderBy(m => m.Version))
        {
            if (migration.Version <= current || (targetVersion is not null && migration.Version > targetVersion))
            {
                continue;
            }

            using var transaction = connection.BeginTransaction();
            connection.Execute(migration.Sql, transaction);
            using (var record = connection.Command(
                       "INSERT INTO schema_version (version, applied_utc, description) VALUES ($version, $applied, $description);",
                       transaction))
            {
                record.With("$version", migration.Version)
                    .With("$applied", UnixTime.ToMs(time.GetUtcNow()))
                    .With("$description", migration.Description)
                    .ExecuteNonQuery();
            }

            // PRAGMA arguments cannot be parameters; the value is an integer constant from code, never user input.
            connection.Execute(
                string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {migration.Version};"),
                transaction);
            transaction.Commit();
            current = migration.Version;
        }
    }
}
