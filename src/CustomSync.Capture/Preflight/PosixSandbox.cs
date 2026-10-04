using System.Runtime.InteropServices;

namespace CustomSync.Capture.Preflight;

public interface IPosixFileSystem
{
    void SetUmask(int mask);
    void CreateDirectory0700(string path);
    uint GetCurrentUserId();
    bool TryGetPathPermissions(string path, out uint ownerUid, out UnixFileMode mode);
}

public class SystemPosixFileSystem : IPosixFileSystem
{
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(20)]
        public uint stx_uid;

        [FieldOffset(28)]
        public ushort stx_mode;
    }

    [DllImport("libc", EntryPoint = "geteuid", SetLastError = true)]
    private static extern uint geteuid();

    [DllImport("libc", EntryPoint = "umask", SetLastError = true)]
    private static extern int umask(int mask);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int statx(int dirfd, string pathname, int flags, uint mask, out Statx statxbuf);

    public void SetUmask(int mask)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                umask(mask);
            }
            catch
            {
                // Fallback / ignore if P/Invoke is unavailable
            }
        }
    }

    public void CreateDirectory0700(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (!Directory.Exists(path))
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            else
            {
                Directory.CreateDirectory(path);
            }
        }
    }

    public uint GetCurrentUserId()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                return geteuid();
            }
            catch
            {
                return 0;
            }
        }
        return 0;
    }

    public bool TryGetPathPermissions(string path, out uint ownerUid, out UnixFileMode mode)
    {
        ownerUid = 0;
        mode = (UnixFileMode)0;

        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                mode = File.GetUnixFileMode(path);
            }
            else
            {
                return false;
            }

            if (OperatingSystem.IsLinux())
            {
                // AT_FDCWD = -100, STATX_BASIC_STATS = 0x7FF
                if (statx(-100, path, 0, 0x7FF, out var stx) == 0)
                {
                    ownerUid = stx.stx_uid;
                }
                else
                {
                    ownerUid = geteuid();
                }
            }
            else
            {
                ownerUid = 0;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}

public static class PosixSandbox
{
    private static IPosixFileSystem _current = new SystemPosixFileSystem();

    public static IPosixFileSystem Current
    {
        get => _current;
        set => _current = value ?? new SystemPosixFileSystem();
    }

    public static void ResetToDefault()
    {
        _current = new SystemPosixFileSystem();
    }

    public static void SetUmask0077()
    {
        // 0077 octal is 63 decimal
        _current.SetUmask(0x3F);
    }

    public static void CreateDirectory0700(string path)
    {
        _current.CreateDirectory0700(path);
    }
}
