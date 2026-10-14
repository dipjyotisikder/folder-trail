using System.Runtime.Versioning;
using FolderTrail.Core.Features.Settings;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;

namespace FolderTrail.App.Shell;

/// <summary>
/// "Start with Windows" for the MSIX build. Registry Run-key writes are virtualized inside a package and never
/// run at sign-in, so the package declares a startup task (see Package/AppxManifest.xml) and this toggles it.
/// The user can also switch it off in Settings → Apps → Startup; the app then can't turn it back on.
/// </summary>

public sealed class PackagedStartupRegistration : IStartupRegistration
{
    public const string TaskId = "FolderTrailStartup";

    /// <summary>A startup task can't pass arguments, so a sign-in launch is recognised by how Windows activated the app.</summary>
    public static bool WasLaunchedByStartupTask()
    {
        try
        {
            return AppInstance.GetActivatedEventArgs()?.Kind == ActivationKind.StartupTask;
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return false;
        }
    }

    public bool IsSupported => true;

    public bool IsEnabled() => Run(async () => IsOn((await StartupTask.GetAsync(TaskId)).State));

    public void SetEnabled(bool enabled) => Run(async () =>
    {
        var task = await StartupTask.GetAsync(TaskId);
        if (!enabled)
        {
            task.Disable();
            return true;
        }

        if (task.State is StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy)
        {
            throw new UnauthorizedAccessException(
                "Windows has blocked FolderTrail from starting at sign-in. Allow it under Settings → Apps → Startup.");
        }

        var state = await task.RequestEnableAsync();
        return IsOn(state)
            ? true
            : throw new UnauthorizedAccessException("Windows didn't allow FolderTrail to start at sign-in.");
    });

    private static bool IsOn(StartupTaskState state) => state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;

    // WinRT calls complete on their own threads; run them off the caller (often the UI thread) so blocking can't deadlock.
    private static T Run<T>(Func<Task<T>> operation)
    {
        try
        {
            return Task.Run(operation).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            throw new PlatformNotSupportedException("Windows couldn't change the startup task: " + exception.Message, exception);
        }
    }
}
