using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NFour.AutoUpdater.Client;

/// Handle-relative, no-follow mutations for POSIX and Windows installs. The Windows adapter
/// uses FILE_FLAG_OPEN_REPARSE_POINT handles and SetFileInformationByHandle for the actual
/// rename/disposition operations, so validation and mutation share an opened handle.
internal static class SecureInstallOperations
{
    // Open flags and AT_* values differ between Linux and Darwin; see PosixPlatform.
    // errno values and permission bits agree, so they stay local.
    private const int ENOENT = 2;
    private const int ENOTDIR = 20;
    private const int EEXIST = 17;
    private const uint Mode0644 = 0x1A4;
    private const uint Mode0755 = 0x1ED;
    private const ulong FICLONE = 0x40049409;

    private static int DirectoryOpenFlags => PosixPlatform.DirectoryOpenFlags;
    private static int FileReadFlags => PosixPlatform.FileReadFlags;
    private static int FileCreateFlags => PosixPlatform.FileCreateFlags;

    public static bool Supported => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

    public static bool TryEnsureDirectory(string root, VirtualPath path, FileIdentity? expectedParent = null)
    {
        if (OperatingSystem.IsWindows()) return WindowsSecureInstallOperations.TryEnsureDirectory(root, path, expectedParent);
        if (!Supported) return false;
        using var rootHandle = OpenDirectory(root);
        using var current = OpenOrCreateChildren(rootHandle, path.Value.Split('/'), expectedParent, path);
        return true;
    }

    public static bool TryFileExists(string root, VirtualPath path, FileIdentity? expectedParent = null)
    {
        if (OperatingSystem.IsWindows()) return WindowsSecureInstallOperations.TryFileExists(root, path, expectedParent);
        if (!Supported) return false;
        var components = path.Value.Split('/');
        using var rootHandle = OpenDirectory(root);
        using var parent = OpenChildren(rootHandle, components[..^1]);
        if (parent is null) return false;
        VerifyParent(parent, expectedParent, path);

        var directoryDescriptor = OpenAt(parent, components[^1], DirectoryOpenFlags, 0);
        if (directoryDescriptor >= 0)
        {
            using var directory = new SafeFileHandle((IntPtr)directoryDescriptor, ownsHandle: true);
            return false;
        }

        var directoryError = Marshal.GetLastWin32Error();
        if (directoryError != ENOTDIR && directoryError != ENOENT)
            ThrowLastError($"inspect managed path '{path}'");

        var fileDescriptor = OpenAt(parent, components[^1], FileReadFlags, 0);
        if (fileDescriptor >= 0)
        {
            using var file = new SafeFileHandle((IntPtr)fileDescriptor, ownsHandle: true);
            return true;
        }

        var fileError = Marshal.GetLastWin32Error();
        if (fileError == ENOENT) return false;
        ThrowLastError($"inspect managed path '{path}'");
        return false;
    }

    /// <summary>Atomically replaces a file through platform no-follow operations.</summary>
    /// <param name="root">The trusted install root.</param>
    /// <param name="path">The destination path relative to the root.</param>
    /// <param name="stagedPath">The verified staged source file.</param>
    /// <param name="mode">
    /// POSIX permission bits to apply. Resolved by the caller from the manifest-declared mode
    /// and the install policy, so an arbitrary declared mode is honoured rather than every
    /// executable silently receiving 0755.
    /// </param>
    /// <param name="expectedParent">The parent identity captured during planning.</param>
    /// <param name="preferHardLink">Whether an immutable install may share the staged inode.</param>
    public static bool TryReplaceFile(string root, VirtualPath path, string stagedPath, uint mode = PosixFileMode.DefaultRegular, FileIdentity? expectedParent = null, bool preferHardLink = false)
    {
        if (OperatingSystem.IsWindows()) return WindowsSecureInstallOperations.TryReplaceFile(root, path, stagedPath, expectedParent, preferHardLink);
        if (!Supported) return false;
        var components = path.Value.Split('/');
        using var rootHandle = OpenDirectory(root);
        using var parent = OpenOrCreateChildren(rootHandle, components[..^1]);
        VerifyParent(parent, expectedParent, path);
        var name = components[^1];
        var temporaryName = ".4sup-new-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        // A hardlink shares the inode with the shared cache, so its permissions cannot be
        // adjusted independently. It is only usable when the target mode already matches the
        // mode the cache objects are stored with.
        if (preferHardLink && mode == PosixFileMode.DefaultRegular && LinkAt(PosixPlatform.AT_FDCWD, stagedPath, parent, temporaryName, 0) == 0)
        {
            try
            {
                if (RenameAt(parent, temporaryName, parent, name) != 0) ThrowLastError($"replace '{path}'");
                return true;
            }
            finally { _ = UnlinkAt(parent, temporaryName, 0); }
        }
        var descriptor = OpenAt(parent, temporaryName, FileCreateFlags, Mode0644);
        if (descriptor < 0) ThrowLastError($"create temporary file '{path}'");
        try
        {
            using (var destination = new FileStream(new SafeFileHandle((IntPtr)descriptor, ownsHandle: true), FileAccess.Write, 128 * 1024, isAsync: false))
            {
                var cloned = OperatingSystem.IsLinux() && TryReflink(descriptor, stagedPath);
                // open/openat take the creation mode as a C variadic argument. On Apple
                // arm64 that argument follows the platform's variadic ABI, which a fixed
                // P/Invoke declaration does not reliably populate. Always set the final
                // mode through the non-variadic descriptor API instead of assuming the
                // create call (or the process umask) produced the requested permissions.
                if (Fchmod(descriptor, mode) != 0)
                    ThrowLastError($"set mode {PosixFileMode.Format(mode)} for '{path}'");
                if (!cloned) awaitCopy(stagedPath, destination);
            }
            if (RenameAt(parent, temporaryName, parent, name) != 0)
                ThrowLastError($"replace '{path}'");
            return true;
        }
        finally
        {
            _ = UnlinkAt(parent, temporaryName, 0);
        }
    }

    public static bool TryDeleteFile(string root, VirtualPath path, FileIdentity? expectedParent = null)
    {
        if (OperatingSystem.IsWindows()) return WindowsSecureInstallOperations.TryDeleteFile(root, path, expectedParent);
        if (!Supported) return false;
        var components = path.Value.Split('/');
        using var rootHandle = OpenDirectory(root);
        using var parent = OpenChildren(rootHandle, components[..^1]);
        if (parent is null) return true;
        VerifyParent(parent, expectedParent, path);
        var result = UnlinkAt(parent, components[^1], 0);
        if (result != 0 && Marshal.GetLastWin32Error() != ENOENT) ThrowLastError($"delete '{path}'");
        return true;
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        var descriptor = Open(path, DirectoryOpenFlags, 0);
        if (descriptor < 0) ThrowLastError($"open install root '{path}'");
        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    private static void VerifyParent(SafeFileHandle parent, FileIdentity? expected, VirtualPath path)
    {
        if (expected is null) return;
        if (!FileIdentityProvider.TryGet(parent, out var actual) || actual != expected.Value)
            throw new ApplyPreconditionException($"Parent directory identity changed before mutating '{path}'.");
    }

    private static SafeFileHandle OpenOrCreateChildren(SafeFileHandle root, IReadOnlyList<string> components, FileIdentity? expectedParent = null, VirtualPath? path = null)
    {
        var current = new SafeFileHandle(root.DangerousGetHandle(), ownsHandle: false);
        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];
            if (string.IsNullOrEmpty(component)) continue;
            if (index == components.Count - 1 && path is not null) VerifyParent(current, expectedParent, path.Value);
            var made = MkdirAt(current, component, Mode0755);
            if (made != 0 && Marshal.GetLastWin32Error() != EEXIST) ThrowLastError($"create managed directory '{component}'");
            var next = OpenAtHandle(current, component, DirectoryOpenFlags);
            current.Dispose();
            current = next;
        }
        return current;
    }

    private static SafeFileHandle? OpenChildren(SafeFileHandle root, IReadOnlyList<string> components)
    {
        var current = new SafeFileHandle(root.DangerousGetHandle(), ownsHandle: false);
        foreach (var component in components)
        {
            if (string.IsNullOrEmpty(component)) continue;
            var nextDescriptor = OpenAt(current, component, DirectoryOpenFlags, 0);
            if (nextDescriptor < 0)
            {
                current.Dispose();
                return Marshal.GetLastWin32Error() == ENOENT ? null : throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            current.Dispose();
            current = new SafeFileHandle((IntPtr)nextDescriptor, ownsHandle: true);
        }
        return current;
    }

    private static SafeFileHandle OpenAtHandle(SafeFileHandle directory, string name, int flags)
    {
        var descriptor = OpenAt(directory, name, flags, 0);
        if (descriptor < 0) ThrowLastError($"open managed directory '{name}'");
        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    private static void awaitCopy(string sourcePath, FileStream destination)
    {
        using var source = File.OpenRead(sourcePath);
        source.CopyTo(destination);
        destination.Flush(flushToDisk: true);
    }

    private static void ThrowLastError(string operation) => throw new IOException($"Unable to {operation}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

    private static bool TryReflink(int destinationDescriptor, string sourcePath)
    {
        var sourceDescriptor = Open(sourcePath, FileReadFlags, 0);
        if (sourceDescriptor < 0) return false;
        using var source = new SafeFileHandle((IntPtr)sourceDescriptor, ownsHandle: true);
        return Ioctl(destinationDescriptor, FICLONE, sourceDescriptor) == 0;
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "open")] private static extern int Open(string pathname, int flags, uint mode);
    [DllImport("libc", SetLastError = true, EntryPoint = "openat")] private static extern int OpenAt(SafeFileHandle dirfd, string pathname, int flags, uint mode);
    [DllImport("libc", SetLastError = true, EntryPoint = "mkdirat")] private static extern int MkdirAt(SafeFileHandle dirfd, string pathname, uint mode);
    [DllImport("libc", SetLastError = true, EntryPoint = "fchmod")] private static extern int Fchmod(int fd, uint mode);
    [DllImport("libc", SetLastError = true, EntryPoint = "renameat")] private static extern int RenameAt(SafeFileHandle olddirfd, string oldpath, SafeFileHandle newdirfd, string newpath);
    [DllImport("libc", SetLastError = true, EntryPoint = "linkat")] private static extern int LinkAt(int olddirfd, string oldpath, SafeFileHandle newdirfd, string newpath, int flags);
    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")] private static extern int Ioctl(int fileDescriptor, ulong request, int argument);
    [DllImport("libc", SetLastError = true, EntryPoint = "unlinkat")] private static extern int UnlinkAt(SafeFileHandle dirfd, string pathname, int flags);
}
