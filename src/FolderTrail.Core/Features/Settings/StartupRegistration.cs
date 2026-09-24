using System.Runtime.Versioning;
using Microsoft.Win32;

namespace FolderTrail.Core.Features.Settings;

public static class StartupArguments
{
    public const string Minimized = "--minimized";
}

public interface IStartupRegistration
{
    bool IsSupported { get; }

    bool IsEnabled();

    void SetEnabled(bool enabled);
}

/// <summary>
/// Uses the per-user Run key (HKCU), so no administrator rights are needed. Disabling deletes the value.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartupRegistration(string executablePath) : IStartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "FolderTrail";

    public bool IsSupported => true;

    public string Command => $"\"{executablePath}\" {StartupArguments.Minimized}";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && value.Length > 0;
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

public sealed class UnsupportedStartupRegistration : IStartupRegistration
{
    public bool IsSupported => false;

    public bool IsEnabled() => false;

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            throw new PlatformNotSupportedException("Starting with the operating system is only supported on Windows.");
        }
    }
}
