using System.Runtime.InteropServices;
using CustomSync.Capture.Preflight;
using Microsoft.Extensions.Configuration;

namespace CustomSync.Capture.Tdlib;

public static class DatabaseEncryptionKey
{
    public const int MinimumKeyBytes = 32;

    public static string? ResolveKeyFilePath(IConfiguration config)
    {
        var configured = config["Telegram:DatabaseEncryptionKeyFile"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var credDir = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(credDir))
        {
            return Path.Combine(credDir, "tdlib-db-key");
        }

        return null;
    }

    public static (bool Success, string? KeyBase64, string? Error) LoadKey(IConfiguration config)
    {
        var path = ResolveKeyFilePath(config);
        if (string.IsNullOrWhiteSpace(path))
        {
            return (false, null, "Telegram:DatabaseEncryptionKeyFile is not configured and tdlib-db-key credential was not found in CREDENTIALS_DIRECTORY.");
        }

        if (!File.Exists(path))
        {
            return (false, null, $"Database encryption key file '{path}' does not exist or cannot be read.");
        }

        // Permission check on Unix: if outside $CREDENTIALS_DIRECTORY, group/other bits must not be set
        var credDir = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
        bool isInCredentialsDir = !string.IsNullOrWhiteSpace(credDir) && IsInsideDirectory(path, credDir);

        if (!isInCredentialsDir)
        {
            var probe = PosixSandbox.Current.ProbePathPermissions(path, out _, out var mode);
            if (probe == PathPermissionProbe.Unreadable)
            {
                return (false, null, $"Telegram:DatabaseEncryptionKeyFile '{path}': its permissions cannot be verified (expected <= 0600).");
            }

            // Group or other bits check (octal 0077 is 0x3F)
            if (probe == PathPermissionProbe.Read && (mode & (UnixFileMode)0x03F) != 0)
            {
                return (false, null, $"Telegram:DatabaseEncryptionKeyFile '{path}' has permissions {mode}. Group and other bits must not be set (expected <= 0600).");
            }
        }

        string content;
        try
        {
            content = File.ReadAllText(path).Trim();
        }
        catch (Exception ex)
        {
            return (false, null, $"Database encryption key file '{path}' could not be read: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return (false, null, $"Database encryption key file '{path}' is empty.");
        }

        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(content);
        }
        catch (FormatException)
        {
            return (false, null, $"Database encryption key file '{path}' does not contain valid base64 text.");
        }

        if (keyBytes.Length < MinimumKeyBytes)
        {
            return (false, null, $"Database encryption key in '{path}' must be at least {MinimumKeyBytes} bytes (decoded {keyBytes.Length} bytes).");
        }

        return (true, content, null);
    }

    // A plain prefix test let `/run/credentials/x-evil/key` and
    // `$CREDENTIALS_DIRECTORY/../key` skip the mode check.
    private static bool IsInsideDirectory(string path, string directory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(fullDirectory, comparison);
    }
}
