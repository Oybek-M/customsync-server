using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CustomSync.Capture.Capture;
using Microsoft.Extensions.Logging;

namespace CustomSync.Capture.Media;

public class MediaStore
{
    private static readonly Regex FinalNameRegex = new(@"^[1-9][0-9]*-[1-9][0-9]*\.bin$", RegexOptions.Compiled);
    private static readonly Regex PartNameRegex = new(@"^[1-9][0-9]*-[1-9][0-9]*\.bin\.part$", RegexOptions.Compiled);

    private readonly string _storageDirectory;

    public string StorageDirectory => _storageDirectory;

    public MediaStore(string storageDirectory)
    {
        if (string.IsNullOrWhiteSpace(storageDirectory))
            throw new ArgumentException("Storage directory cannot be empty.", nameof(storageDirectory));

        _storageDirectory = Path.GetFullPath(storageDirectory);
    }

    public string PathFor(string peerId, long msgId)
    {
        ValidatePeerAndMsgId(peerId, msgId);
        return Path.Combine(_storageDirectory, $"{peerId}-{msgId}.bin");
    }

    public string PathFor(long peerId, long msgId) =>
        PathFor(peerId.ToString(System.Globalization.CultureInfo.InvariantCulture), msgId);

    public string TempPathFor(string peerId, long msgId)
    {
        ValidatePeerAndMsgId(peerId, msgId);
        return Path.Combine(_storageDirectory, $"{peerId}-{msgId}.bin.part");
    }

    public string TempPathFor(long peerId, long msgId) =>
        TempPathFor(peerId.ToString(System.Globalization.CultureInfo.InvariantCulture), msgId);

    public Task<(string Path, string Sha256, long Size)> CopyInAsync(
        long peerId,
        long msgId,
        string sourcePath,
        long maxBytes,
        CancellationToken ct = default) =>
        CopyInAsync(sourcePath, peerId.ToString(System.Globalization.CultureInfo.InvariantCulture), msgId, maxBytes, ct);

    public Task<(string Path, string Sha256, long Size)> CopyInAsync(
        string peerId,
        long msgId,
        string sourcePath,
        long maxBytes,
        CancellationToken ct = default) =>
        CopyInAsync(sourcePath, peerId, msgId, maxBytes, ct);

    public bool IsManaged(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string fullPath = Path.GetFullPath(path);
        string? parent = Path.GetDirectoryName(fullPath);
        if (parent is null)
            return false;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(parent, _storageDirectory, comparison))
            return false;

        string fileName = Path.GetFileName(fullPath);
        return FinalNameRegex.IsMatch(fileName);
    }

    public bool IsManagedPart(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string fullPath = Path.GetFullPath(path);
        string? parent = Path.GetDirectoryName(fullPath);
        if (parent is null)
            return false;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(parent, _storageDirectory, comparison))
            return false;

        string fileName = Path.GetFileName(fullPath);
        return PartNameRegex.IsMatch(fileName);
    }

    public void EnsureDirectoryCreated()
    {
        if (File.Exists(_storageDirectory))
            throw new InvalidOperationException($"Media storage directory '{_storageDirectory}' exists as a regular file.");

        if (!Directory.Exists(_storageDirectory))
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(_storageDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            else
            {
                Directory.CreateDirectory(_storageDirectory);
            }
        }
    }

    public async Task<(string Path, string Sha256, long Size)> CopyInAsync(
        string sourcePath,
        string peerId,
        long msgId,
        long maxBytes,
        CancellationToken ct = default)
    {
        EnsureDirectoryCreated();

        string finalPath = PathFor(peerId, msgId);
        string tempPath = TempPathFor(peerId, msgId);

        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
        }

        FileStream? destination = null;
        FileStream? source = null;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                destination = new FileStream(tempPath, new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 4096,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                });
            }
            else
            {
                destination = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.None);
            }

            source = File.OpenRead(sourcePath);
            byte[] buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                totalRead += bytesRead;
                if (totalRead > maxBytes)
                {
                    destination.Dispose();
                    destination = null;
                    try { File.Delete(tempPath); } catch { }
                    throw new MediaFileTooLargeException($"File exceeds maximum allowed size of {maxBytes} bytes.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                hasher.AppendData(buffer, 0, bytesRead);
            }

            await destination.FlushAsync(ct);
            destination.Dispose();
            destination = null;

            source.Dispose();
            source = null;

            var sha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();

            File.Move(tempPath, finalPath, overwrite: true);

            return (finalPath, sha256, totalRead);
        }
        catch
        {
            destination?.Dispose();
            source?.Dispose();
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
            }
            throw;
        }
    }

    public (long TotalBytes, int FileCount) MeasureStore()
    {
        if (!Directory.Exists(_storageDirectory))
            return (0, 0);

        long totalBytes = 0;
        int count = 0;

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(_storageDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                if (IsManaged(filePath))
                {
                    try
                    {
                        var fi = new FileInfo(filePath);
                        totalBytes += fi.Length;
                        count++;
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }

        return (totalBytes, count);
    }

    public void MeasureStore(out long totalBytes, out int fileCount)
    {
        var res = MeasureStore();
        totalBytes = res.TotalBytes;
        fileCount = res.FileCount;
    }

    public int SweepOrphans(MessageCache cache, long now, ILogger? logger = null)
    {
        if (!Directory.Exists(_storageDirectory))
            return 0;

        int deletedCount = 0;
        var cutoff = DateTimeOffset.FromUnixTimeSeconds(now - 3600).UtcDateTime;

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(_storageDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                // Must be either managed final pattern or managed temporary pattern
                if (!IsManaged(filePath) && !IsManagedPart(filePath))
                    continue;

                try
                {
                    var fi = new FileInfo(filePath);
                    if (fi.LastWriteTimeUtc >= cutoff)
                        continue; // Less than 1 hour old -> keep

                    if (cache.IsMediaReferenced(filePath))
                        continue; // Referenced by a captured_media row -> keep

                    fi.Delete();
                    deletedCount++;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning("Failed to delete orphaned file ({ErrorType}).", ex.GetType().Name);
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning("Failed to enumerate media store during orphan sweep ({ErrorType}).", ex.GetType().Name);
        }

        return deletedCount;
    }

    private static void ValidatePeerAndMsgId(string peerId, long msgId)
    {
        if (!ScopeConfigReader.IsCanonicalPeerId(peerId))
            throw new ArgumentException($"'{peerId}' is not a canonical peer ID.", nameof(peerId));

        if (msgId <= 0)
            throw new ArgumentException($"Message ID must be positive, got {msgId}.", nameof(msgId));
    }
}

public class MediaFileTooLargeException : Exception
{
    public MediaFileTooLargeException(string message) : base(message)
    {
    }
}

