using FolderTrail.Core.SharedKernel.Persistence;
using FolderTrail.Tests.Support;
using Microsoft.Data.Sqlite;

namespace FolderTrail.Tests.SharedKernel;

public sealed class DatabaseMigrationTests
{
    [Fact]
    public void A_new_database_is_created_at_the_latest_schema_version_with_history()
    {
        using var test = new TestDatabase();
        using var connection = test.Database.OpenConnection();

        Assert.Equal(SchemaMigrations.LatestVersion, DatabaseMigrator.GetUserVersion(connection));
        Assert.Equal(SchemaMigrations.LatestVersion, test.Database.SchemaVersion);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_version ORDER BY version;";
        using var reader = command.ExecuteReader();
        var versions = new List<long>();
        while (reader.Read())
        {
            versions.Add(reader.GetInt64(0));
        }

        Assert.Equal(SchemaMigrations.All.Select(m => (long)m.Version), versions);
    }

    [Fact]
    public void Required_tables_and_timeline_indexes_exist()
    {
        using var test = new TestDatabase();
        using var connection = test.Database.OpenConnection();
        var names = Query(connection, "SELECT name FROM sqlite_master WHERE type IN ('table', 'index');");

        foreach (var table in new[] { "schema_version", "watched_roots", "inventory", "activity_events", "exclusions", "monitoring_gaps", "app_settings" })
        {
            Assert.Contains(table, names);
        }

        foreach (var index in new[] { "ix_events_time", "ix_events_root_time", "ix_events_type_time", "ix_events_path", "ix_events_old_path" })
        {
            Assert.Contains(index, names);
        }
    }

    [Fact]
    public void Upgrading_from_version_1_keeps_existing_data_and_adds_new_columns()
    {
        using var v1 = new TestDatabase(migrations: [SchemaMigrations.V1]);
        using (var connection = v1.Database.OpenConnection())
        {
            Assert.Equal(1, DatabaseMigrator.GetUserVersion(connection));
            connection.Execute(
                """
                INSERT INTO watched_roots (id, display_path, path_key, include_subfolders, added_utc) VALUES (1, 'C:\Data', 'C:\DATA', 1, 0);
                INSERT INTO activity_events (root_id, detected_utc, event_type, source, kind, path, path_key, name)
                VALUES (1, 1000, 1, 1, 1, 'C:\Data\a.txt', 'C:\DATA\A.TXT', 'a.txt');
                """);
        }

        var upgraded = new AppDatabase(new DatabaseOptions(v1.Path, Pooling: false), v1.Time);
        upgraded.Initialize();

        using var check = upgraded.OpenConnection();
        Assert.Equal(SchemaMigrations.LatestVersion, DatabaseMigrator.GetUserVersion(check));
        using var command = check.CreateCommand();
        command.CommandText = "SELECT path, merged_count, previous_length FROM activity_events;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(@"C:\Data\a.txt", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.True(reader.IsDBNull(2));
    }

    [Fact]
    public void Migrating_twice_is_a_no_op()
    {
        using var test = new TestDatabase();
        test.Database.Initialize();
        using var connection = test.Database.OpenConnection();
        Assert.Equal(SchemaMigrations.All.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), Query(connection, "SELECT COUNT(*) FROM schema_version;").Single());
    }

    [Fact]
    public void A_database_from_a_newer_app_version_is_refused_rather_than_damaged()
    {
        using var test = new TestDatabase();
        using (var connection = test.Database.OpenConnection())
        {
            connection.Execute("PRAGMA user_version = 99;");
        }

        var exception = Assert.Throws<UnsupportedSchemaVersionException>(() => test.Database.Initialize());
        Assert.Equal(99, exception.DatabaseVersion);
    }

    [Fact]
    public void Deleting_a_watched_root_cascades_to_its_history()
    {
        using var test = new TestDatabase();
        using var connection = test.Database.OpenConnection();
        connection.Execute(
            """
            INSERT INTO watched_roots (id, display_path, path_key, include_subfolders, added_utc) VALUES (5, 'C:\X', 'C:\X', 1, 0);
            INSERT INTO activity_events (root_id, detected_utc, event_type, source, kind, path, path_key, name) VALUES (5, 1, 1, 1, 1, 'C:\X\a', 'C:\X\A', 'a');
            INSERT INTO inventory (root_id, path_key, display_path, kind, recorded_utc) VALUES (5, 'C:\X\A', 'C:\X\a', 1, 1);
            INSERT INTO monitoring_gaps (root_id, reason, start_utc) VALUES (5, 1, 1);
            DELETE FROM watched_roots WHERE id = 5;
            """);

        Assert.Equal("0", Query(connection, "SELECT (SELECT COUNT(*) FROM activity_events) + (SELECT COUNT(*) FROM inventory) + (SELECT COUNT(*) FROM monitoring_gaps);").Single());
    }

    [Fact]
    public async Task Writes_are_serialized_and_all_succeed_under_concurrency()
    {
        using var test = new TestDatabase();
        var writes = Enumerable.Range(0, 40).Select(i => test.Database.WriteAsync(
            (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO app_settings (key, value) VALUES ($k, 'v');";
                command.Parameters.AddWithValue("$k", "key" + i);
                command.ExecuteNonQuery();
            },
            TestContext.Current.CancellationToken));
        await Task.WhenAll(writes);

        var count = await test.Database.ReadAsync(
            connection => Query(connection, "SELECT COUNT(*) FROM app_settings;").Single(),
            TestContext.Current.CancellationToken);
        Assert.Equal("40", count);
    }

    [Fact]
    public void Busy_and_locked_errors_are_treated_as_transient()
    {
        Assert.True(AppDatabase.IsTransient(new SqliteException("busy", 5)));
        Assert.True(AppDatabase.IsTransient(new SqliteException("locked", 6)));
        Assert.False(AppDatabase.IsTransient(new SqliteException("constraint", 19)));
    }

    private static List<string> Query(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
        }

        return values;
    }
}
