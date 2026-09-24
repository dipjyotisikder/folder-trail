using System.Globalization;

namespace FolderTrail.App.Shared;

/// <summary>Display formatting. Values are stored in UTC and rendered in the user's local time and culture.</summary>
public static class Format
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB"];

    public static string Size(long? bytes)
    {
        if (bytes is not { } value)
        {
            return "Unknown";
        }

        return value < 1024
            ? $"{value.ToString("N0", CultureInfo.CurrentCulture)} bytes"
            : $"{ShortSize(value)} ({value.ToString("N0", CultureInfo.CurrentCulture)} bytes)";
    }

    public static string ShortSize(long bytes)
    {
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes.ToString("N0", CultureInfo.CurrentCulture)} bytes" : $"{size.ToString(size < 10 ? "0.0" : "0", CultureInfo.CurrentCulture)} {Units[unit]}";
    }

    public static DateTimeOffset ToLocal(DateTimeOffset utc, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(utc, zone);

    public static string LocalDateTime(DateTimeOffset utc, TimeZoneInfo zone) => ToLocal(utc, zone).ToString("F", CultureInfo.CurrentCulture);

    public static string LocalTime(DateTimeOffset utc, TimeZoneInfo zone) => ToLocal(utc, zone).ToString("T", CultureInfo.CurrentCulture);

    public static string Utc(DateTimeOffset utc) => utc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    public static string Relative(DateTimeOffset? utc, DateTimeOffset now, TimeZoneInfo zone, string never = "Never")
    {
        if (utc is not { } value)
        {
            return never;
        }

        var elapsed = now - value;
        if (elapsed < TimeSpan.FromSeconds(45))
        {
            return "Just now";
        }

        if (elapsed < TimeSpan.FromMinutes(1.5))
        {
            return "1 minute ago";
        }

        if (elapsed < TimeSpan.FromMinutes(60))
        {
            return $"{(int)Math.Round(elapsed.TotalMinutes)} minutes ago";
        }

        if (elapsed < TimeSpan.FromHours(1.5))
        {
            return "1 hour ago";
        }

        if (elapsed < TimeSpan.FromHours(24))
        {
            return $"{(int)Math.Round(elapsed.TotalHours)} hours ago";
        }

        var local = ToLocal(value, zone);
        var today = DateOnly.FromDateTime(ToLocal(now, zone).DateTime);
        return DateOnly.FromDateTime(local.DateTime) == today.AddDays(-1)
            ? "Yesterday at " + local.ToString("t", CultureInfo.CurrentCulture)
            : local.ToString("g", CultureInfo.CurrentCulture);
    }

    public static string DayHeader(DateOnly day, DateOnly today)
    {
        if (day == today)
        {
            return "Today";
        }

        if (day == today.AddDays(-1))
        {
            return "Yesterday";
        }

        return day.ToDateTime(TimeOnly.MinValue).ToString("D", CultureInfo.CurrentCulture);
    }

    public static string Count(long value, string singular, string plural) =>
        $"{value.ToString("N0", CultureInfo.CurrentCulture)} {(value == 1 ? singular : plural)}";
}
