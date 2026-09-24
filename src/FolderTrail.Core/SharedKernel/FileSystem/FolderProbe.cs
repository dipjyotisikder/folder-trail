using System.Security;
using FolderTrail.Core.SharedKernel.Paths;

namespace FolderTrail.Core.SharedKernel.FileSystem;

public enum FolderAvailability
{
    Available,
    FolderMissing,
    DriveUnavailable,
    AccessDenied,
    NotADirectory,
    Error,
}

public sealed record FolderProbeResult(FolderAvailability Availability, string? Detail = null)
{
    public static FolderProbeResult Available { get; } = new(FolderAvailability.Available);

    public bool IsAvailable => Availability == FolderAvailability.Available;

    public string Describe(string canonicalPath) => Availability switch
    {
        FolderAvailability.Available => "The folder is available.",
        FolderAvailability.FolderMissing =>
            "This folder can't be found. It may have been moved, renamed, or deleted. FolderTrail will not recreate it.",
        FolderAvailability.DriveUnavailable =>
            $"The drive or network location {WindowsPath.GetVolumeRoot(canonicalPath)} isn't available. It may be disconnected. FolderTrail checks again automatically.",
        FolderAvailability.AccessDenied => "FolderTrail doesn't have permission to read this folder.",
        FolderAvailability.NotADirectory => "This path points to a file, not a folder.",
        _ => "The folder can't be read right now." + (Detail is null ? string.Empty : " " + Detail),
    };
}

public interface IFolderProbe
{
    FolderProbeResult Probe(string canonicalPath);
}

public sealed class LocalFolderProbe : IFolderProbe
{
    public FolderProbeResult Probe(string canonicalPath)
    {
        try
        {
            if (!Directory.Exists(WindowsPath.GetVolumeRoot(canonicalPath)))
            {
                return new FolderProbeResult(FolderAvailability.DriveUnavailable);
            }

            if (Directory.Exists(canonicalPath))
            {
                try
                {
                    using var entries = Directory.EnumerateFileSystemEntries(canonicalPath).GetEnumerator();
                    entries.MoveNext();
                    return FolderProbeResult.Available;
                }
                catch (UnauthorizedAccessException)
                {
                    return new FolderProbeResult(FolderAvailability.AccessDenied);
                }
                catch (DirectoryNotFoundException)
                {
                    return new FolderProbeResult(FolderAvailability.FolderMissing);
                }
            }

            return File.Exists(canonicalPath)
                ? new FolderProbeResult(FolderAvailability.NotADirectory)
                : new FolderProbeResult(FolderAvailability.FolderMissing);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new FolderProbeResult(FolderAvailability.Error, exception.Message);
        }
    }
}
