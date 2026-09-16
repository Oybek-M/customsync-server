namespace CustomSync.Services.Storage;

public interface IDiskProbe
{
    (long TotalBytes, long FreeBytes)? Probe(string path);
}

public class SystemDiskProbe : IDiskProbe
{
    public (long TotalBytes, long FreeBytes)? Probe(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return null;
            }

            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch
        {
            return null;
        }
    }
}
