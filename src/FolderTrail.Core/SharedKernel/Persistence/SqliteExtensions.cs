using Microsoft.Data.Sqlite;

namespace FolderTrail.Core.SharedKernel.Persistence;

internal static class SqliteExtensions
{
    public static SqliteCommand Command(this SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    public static SqliteCommand With(this SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public static int Execute(this SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.Command(sql, transaction);
        return command.ExecuteNonQuery();
    }

    public static long ScalarInt64(this SqliteCommand command)
    {
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static long? GetNullableInt64(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    public static string? GetNullableString(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static DateTimeOffset? GetNullableTime(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : UnixTime.FromMs(reader.GetInt64(ordinal));

    public static DateTimeOffset GetTime(this SqliteDataReader reader, int ordinal) => UnixTime.FromMs(reader.GetInt64(ordinal));

    public static bool GetBool(this SqliteDataReader reader, int ordinal) => reader.GetInt64(ordinal) != 0;
}
