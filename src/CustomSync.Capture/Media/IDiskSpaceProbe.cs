namespace CustomSync.Capture.Media;

/// <summary>
/// Probes available free disk space on a filesystem.
/// </summary>
public interface IDiskSpaceProbe
{
    /// <summary>
    /// Returns the available free bytes for the filesystem containing <paramref name="path"/>,
    /// or for its nearest existing ancestor directory. Returns null if the free space cannot be determined.
    /// </summary>
    long? GetAvailableFreeBytes(string path);
}

/// <summary>
/// Production implementation of <see cref="IDiskSpaceProbe"/> using <see cref="DriveInfo"/>.
/// </summary>
public class SystemDiskSpaceProbe : IDiskSpaceProbe
{
    public long? GetAvailableFreeBytes(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            string? current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
            {
                current = Path.GetDirectoryName(current);
            }

            if (string.IsNullOrEmpty(current))
                return null;

            var drive = new DriveInfo(current);
            return drive.AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }
}
