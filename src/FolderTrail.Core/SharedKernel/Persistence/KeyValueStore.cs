using Microsoft.Data.Sqlite;

namespace FolderTrail.Core.SharedKernel.Persistence;

/// <summary>Key/value persistence over app_settings. The Settings feature owns user preferences stored here;
/// other features keep small internal markers (for example the last-running heartbeat).</summary>
public sealed class KeyValueStore(AppDatabase database)
{
    public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default) =>
        database.ReadAsync<IReadOnlyDictionary<string, string>>(
            connection =>
            {
                using var command = connection.Command("SELECT key, value FROM app_settings;");
                using var reader = command.ExecuteReader();
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                while (reader.Read())
                {
                    values[reader.GetString(0)] = reader.GetString(1);
                }

                return values;
            },
            cancellationToken);

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => Get(connection, key), cancellationToken);

    public Task SetAsync(string key, string? value, CancellationToken cancellationToken = default) =>
        database.WriteAsync((connection, transaction) => Set(connection, transaction, key, value), cancellationToken);

    public Task SetManyAsync(IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default) =>
        database.WriteAsync(
            (connection, transaction) =>
            {
                foreach (var (key, value) in values)
                {
                    Set(connection, transaction, key, value);
                }
            },
            cancellationToken);

    internal static string? Get(SqliteConnection connection, string key, SqliteTransaction? transaction = null)
    {
        using var command = connection.Command("SELECT value FROM app_settings WHERE key = $key;", transaction).With("$key", key);
        return command.ExecuteScalar() as string;
    }

    /// <param name="value">Null removes the key.</param>
    internal static void Set(SqliteConnection connection, SqliteTransaction transaction, string key, string? value)
    {
        using var command = connection.Command(
                value is null
                    ? "DELETE FROM app_settings WHERE key = $key;"
                    : "INSERT INTO app_settings (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
                transaction)
            .With("$key", key)
            .With("$value", value);
        command.ExecuteNonQuery();
    }
}
