using FolderTrail.Core.SharedKernel;
using Windows.Storage;

namespace FolderTrail.App.Shell;

/// <summary>
/// Chooses the data folder. The packaged (Store) build uses the package's own local folder: it is a real, visible
/// path that "Open data folder" can reach, and Windows removes it on uninstall. Anywhere else, and whenever
/// FOLDERTRAIL_DATA_DIR is set, it is the usual folder under the user's local application data.
/// </summary>
internal static class AppDataLocator
{
    public static AppDataLocation Resolve()
    {
        var overridden = Environment.GetEnvironmentVariable(AppDataLocation.OverrideVariable) is { Length: > 0 };
        return !overridden && PackageIdentity.IsPackaged
            ? new AppDataLocation(ApplicationData.Current.LocalFolder.Path)
            : AppDataLocation.ForCurrentUser();
    }
}
