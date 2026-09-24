namespace FolderTrail.Core.SharedKernel;

/// <summary>
/// App data lives under the current user's local application-data folder (not roaming: the inventory describes
/// this machine's drives), never beside the executable or inside a watched folder.
/// </summary>
public sealed class AppDataLocation(string rootDirectory)
{
    public const string DatabaseFileName = "foldertrail.db";

    public string RootDirectory { get; } = rootDirectory;

    public string DatabasePath => Path.Combine(RootDirectory, DatabaseFileName);

    public string LogDirectory => Path.Combine(RootDirectory, "diagnostics");

    /// <summary>Set to use a different data folder, for example to test without touching the real profile.</summary>
    public const string OverrideVariable = "FOLDERTRAIL_DATA_DIR";

    public static AppDataLocation ForCurrentUser()
    {
        if (Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } overridden)
        {
            return new AppDataLocation(Path.GetFullPath(overridden));
        }

        var baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        return new AppDataLocation(Path.Combine(baseDirectory, "FolderTrail"));
    }
}
