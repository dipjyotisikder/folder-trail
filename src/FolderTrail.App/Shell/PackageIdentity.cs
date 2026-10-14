using System.Runtime.InteropServices;
using System.Text;

namespace FolderTrail.App.Shell;

/// <summary>Whether the process runs from an installed MSIX package (the Microsoft Store build) or as a plain executable.</summary>
internal static class PackageIdentity
{
    private const int AppModelErrorNoPackage = 15700;
    private const int ErrorInsufficientBuffer = 122;

    private static readonly Lazy<bool> Packaged = new(Detect);

    public static bool IsPackaged => Packaged.Value;

    private static bool Detect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var length = 0;
            var result = GetCurrentPackageFullName(ref length, null);
            return result != AppModelErrorNoPackage && (result == ErrorInsufficientBuffer || result == 0);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);
}
