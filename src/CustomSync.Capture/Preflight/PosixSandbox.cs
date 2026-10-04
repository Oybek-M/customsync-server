using System.Runtime.InteropServices;

namespace CustomSync.Capture.Preflight;

public enum PathPermissionProbe
{
    // The platform has no POSIX owner/mode (Windows): nothing to check.
    NotApplicable,
    Read,
    // A POSIX platform, but the owner or mode could not be read. Callers
    // must treat this as a failed check, never as "nothing to check".
    Unreadable,
}

public interface IPosixFileSystem
{
    void SetUmask(int mask);
    void CreateDirectory0700(string path);
    uint GetCurrentUserId();
    PathPermissionProbe ProbePathPermissions(string path, out uint ownerUid, out UnixFileMode mode);
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
        // No catch: a umask that silently failed to apply leaves every file
        // the session writes readable by others. The entry point reports it.
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            umask(mask);
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
        // No fallback to 0 on failure: that is root's id and would make a
        // root-owned directory pass the owner check.
        if (OperatingSystem.IsLinux())
        {
            return geteuid();
        }
        return 0;
    }

    public PathPermissionProbe ProbePathPermissions(string path, out uint ownerUid, out UnixFileMode mode)
    {
        ownerUid = 0;
        mode = (UnixFileMode)0;

        if (OperatingSystem.IsWindows())
        {
            return PathPermissionProbe.NotApplicable;
        }

        // Only Linux has the owner read below; elsewhere the owner would be
        // a guess, so the check cannot pass.
        if (!OperatingSystem.IsLinux())
        {
            return PathPermissionProbe.Unreadable;
        }

        try
        {
            mode = File.GetUnixFileMode(path);

            // AT_FDCWD = -100, STATX_BASIC_STATS = 0x7FF. No fallback to the
            // process's own id when statx fails: that made the owner check
            // pass by construction.
            if (statx(-100, path, 0, 0x7FF, out var stx) != 0)
            {
                return PathPermissionProbe.Unreadable;
            }

            ownerUid = stx.stx_uid;
            return PathPermissionProbe.Read;
        }
        catch (Exception)
        {
            return PathPermissionProbe.Unreadable;
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
