namespace FolderTrail.Core.Features.Settings;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public sealed record AppSettings
{
    public static AppSettings Defaults { get; } = new();

    /// <summary>Null means history is kept without a time limit.</summary>
    public static IReadOnlyList<int?> RetentionChoices { get; } = [30, 90, 180, 365, null];

    public static IReadOnlyList<int> CoalescingChoicesMs { get; } = [500, 1000, 2000, 5000];

    public static IReadOnlyList<int> PeriodicScanChoicesMinutes { get; } = [15, 30, 60, 120, 240];

    public int? RetentionDays { get; init; } = 90;

    public bool CloseToTray { get; init; }

    /// <summary>Mirrors the user's Run registry entry, which is the source of truth.</summary>
    public bool StartWithWindows { get; init; }

    public ThemePreference Theme { get; init; } = ThemePreference.System;

    public int CoalescingWindowMs { get; init; } = 1000;

    public int PeriodicScanMinutes { get; init; } = 60;

    public bool NotifyOnMonitoringProblems { get; init; } = true;

    public bool OnboardingCompleted { get; init; }

    public TimeSpan CoalescingWindow => TimeSpan.FromMilliseconds(CoalescingWindowMs);

    public TimeSpan PeriodicScanInterval => TimeSpan.FromMinutes(PeriodicScanMinutes);

    public AppSettings Normalized() => this with
    {
        RetentionDays = RetentionChoices.Contains(RetentionDays) ? RetentionDays : Defaults.RetentionDays,
        CoalescingWindowMs = CoalescingChoicesMs.Contains(CoalescingWindowMs) ? CoalescingWindowMs : Defaults.CoalescingWindowMs,
        PeriodicScanMinutes = PeriodicScanChoicesMinutes.Contains(PeriodicScanMinutes) ? PeriodicScanMinutes : Defaults.PeriodicScanMinutes,
        Theme = Enum.IsDefined(Theme) ? Theme : ThemePreference.System,
    };
}
