using System.Reflection;
using System.Xml.Linq;
using FolderTrail.App.Shell;
using FolderTrail.Core.Features.Settings;

namespace FolderTrail.Tests.Features.Packaging;

/// <summary>
/// Guards the pieces of the Store package that can't be exercised without installing it: the manifest must keep
/// pointing at what the code expects, and the "nothing leaves your computer" promise in the listing must hold.
/// </summary>
public sealed class StorePackageTests
{
    private static readonly XNamespace Uap5 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/5";
    private static readonly XNamespace Foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace RestrictedCapabilities = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

    [Fact]
    public void Manifest_declares_the_startup_task_the_app_toggles()
    {
        var application = LoadManifest().Descendants(Foundation + "Application").Single();
        var task = application.Descendants(Uap5 + "StartupTask").Single();

        Assert.Equal(PackagedStartupRegistration.TaskId, (string?)task.Attribute("TaskId"));
        Assert.Equal("false", (string?)task.Attribute("Enabled"));
    }

    [Fact]
    public void Manifest_launches_the_executable_the_app_builds()
    {
        var executable = typeof(PackageIdentity).Assembly.GetName().Name + ".exe";
        var application = LoadManifest().Descendants(Foundation + "Application").Single();
        var startup = application.Descendants(Uap5 + "Extension").Single();

        Assert.Equal(executable, (string?)application.Attribute("Executable"));
        Assert.Equal(executable, (string?)startup.Attribute("Executable"));
    }

    [Fact]
    public void Manifest_asks_only_for_the_full_trust_capability()
    {
        var capabilities = LoadManifest().Descendants(Foundation + "Capabilities").Single().Elements().ToList();

        var capability = Assert.Single(capabilities);
        Assert.Equal(RestrictedCapabilities + "Capability", capability.Name);
        Assert.Equal("runFullTrust", (string?)capability.Attribute("Name"));
    }

    [Fact]
    public void Manifest_identity_is_filled_in_at_packaging_time()
    {
        var identity = LoadManifest().Descendants(Foundation + "Identity").Single();

        Assert.Equal("{{IdentityName}}", (string?)identity.Attribute("Name"));
        Assert.Equal("{{Publisher}}", (string?)identity.Attribute("Publisher"));
        Assert.Equal("{{Version}}", (string?)identity.Attribute("Version"));
    }

    [Theory]
    [InlineData("FolderTrail.Core")]
    [InlineData("FolderTrail")]
    public void The_app_contains_no_network_code(string assemblyName)
    {
        var assembly = assemblyName == "FolderTrail.Core" ? typeof(SettingsService).Assembly : typeof(PackageIdentity).Assembly;

        var network = assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith("System.Net.", StringComparison.Ordinal) && name != "System.Net.Primitives")
            .ToList();

        Assert.True(network.Count == 0, $"{assemblyName} references networking assemblies: {string.Join(", ", network)}");
    }

    private static XDocument LoadManifest()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FolderTrail.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return XDocument.Load(Path.Combine(directory.FullName, "packaging", "AppxManifest.xml"));
    }
}
