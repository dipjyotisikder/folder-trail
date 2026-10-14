using FolderTrail.App.Shell;

namespace FolderTrail.Tests.Features.Settings;

public sealed class PackageIdentityTests
{
    [Fact]
    public void A_plain_executable_is_not_packaged()
    {
        Assert.False(PackageIdentity.IsPackaged);
    }

    [Fact]
    public void Startup_task_launch_is_not_reported_without_package_identity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.False(PackagedStartupRegistration.WasLaunchedByStartupTask());
    }
}
