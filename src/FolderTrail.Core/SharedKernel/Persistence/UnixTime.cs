namespace FolderTrail.Core.SharedKernel.Persistence;

/// <summary>All timestamps are stored as UTC Unix milliseconds so they sort and index as integers.</summary>
public static class UnixTime
{
    public static long ToMs(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    public static long? ToMs(DateTimeOffset? value) => value?.ToUnixTimeMilliseconds();

    public static DateTimeOffset FromMs(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);

    public static DateTimeOffset? FromMs(long? value) => value is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(value.Value);

    /// <summary>File times have 100 ns precision but are stored in ms; truncating at the source keeps comparisons stable.</summary>
    public static DateTimeOffset FromFileTimeUtc(DateTime utc) =>
        DateTimeOffset.FromUnixTimeMilliseconds(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds());
}
