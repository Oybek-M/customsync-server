using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CustomSync.Capture.Sync;

public record DeviceState(string DeviceId, string RefreshToken);

public static class DeviceCredentials
{
    public static bool IsModeWiderThan0600(UnixFileMode mode)
    {
        // 0600 is UserRead | UserWrite (0x180).
        // Any bits outside UserRead and UserWrite in standard permissions (0777 / 0x1FF) are wider.
        var allowed = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var forbidden = ~(allowed) & (UnixFileMode)0x1FF;
        return (mode & forbidden) != 0;
    }

    public static bool CheckUnixFilePermissions(string path, ILogger? logger = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // On Windows, skip POSIX file mode check because NTFS uses ACLs rather than POSIX permissions.
            return true;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            if (IsModeWiderThan0600(mode))
            {
                logger?.LogError("File {Path} has insecure permissions {Mode}. Mode must not be wider than 0600.", path, mode);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to read file permissions for {Path}.", path);
            return false;
        }
    }

    public static void WriteAtomic(string targetPath, string content, UnixFileMode unixMode = UnixFileMode.UserRead | UnixFileMode.UserWrite)
    {
        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        else if (string.IsNullOrEmpty(dir))
        {
            dir = ".";
        }

        var tempPath = Path.Combine(dir, $".tmp_{Guid.NewGuid():N}");
        File.WriteAllText(tempPath, content, Encoding.UTF8);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.SetUnixFileMode(tempPath, unixMode);
        }

        File.Move(tempPath, targetPath, overwrite: true);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.SetUnixFileMode(targetPath, unixMode);
        }
    }

    public static byte[]? ParseMasterKeyHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var trimmed = hex.Trim();
        if (trimmed.Length != 64) return null;

        try
        {
            return Convert.FromHexString(trimmed);
        }
        catch
        {
            return null;
        }
    }

    public static byte[]? LoadMasterKey(string path, ILogger? logger = null, Func<string, bool>? permissionChecker = null)
    {
        if (!File.Exists(path))
        {
            logger?.LogWarning("Master key file does not exist at {Path}.", path);
            return null;
        }

        bool permOk = permissionChecker != null ? permissionChecker(path) : CheckUnixFilePermissions(path, logger);
        if (!permOk)
        {
            logger?.LogError("Refusing to load master key from {Path} due to insecure file permissions.", path);
            return null;
        }

        var text = File.ReadAllText(path).Trim();
        var key = ParseMasterKeyHex(text);
        if (key == null)
        {
            logger?.LogError("Master key in {Path} is not a valid 64-character hexadecimal string.", path);
            return null;
        }

        return key;
    }

    public static void SaveMasterKey(string path, byte[] masterKey)
    {
        if (masterKey.Length != 32)
            throw new ArgumentException("Master key must be exactly 32 bytes.", nameof(masterKey));

        var hex = Convert.ToHexString(masterKey).ToLowerInvariant();
        WriteAtomic(path, hex);
    }

    public static DeviceState? LoadDeviceState(string path, ILogger? logger = null, Func<string, bool>? permissionChecker = null)
    {
        if (!File.Exists(path))
        {
            logger?.LogWarning("Device state file does not exist at {Path}.", path);
            return null;
        }

        bool permOk = permissionChecker != null ? permissionChecker(path) : CheckUnixFilePermissions(path, logger);
        if (!permOk)
        {
            logger?.LogError("Refusing to load device state from {Path} due to insecure file permissions.", path);
            return null;
        }

        try
        {
            var text = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var deviceId = root.GetProperty("device_id").GetString();
            var refreshToken = root.GetProperty("refresh_token").GetString();
            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(refreshToken))
            {
                logger?.LogError("Device state file at {Path} is missing device_id or refresh_token.", path);
                return null;
            }
            return new DeviceState(deviceId, refreshToken);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to parse device state file at {Path}.", path);
            return null;
        }
    }

    public static void SaveDeviceState(string path, DeviceState state)
    {
        var json = JsonSerializer.Serialize(new
        {
            device_id = state.DeviceId,
            refresh_token = state.RefreshToken
        }, new JsonSerializerOptions { WriteIndented = true });

        WriteAtomic(path, json);
    }
}
