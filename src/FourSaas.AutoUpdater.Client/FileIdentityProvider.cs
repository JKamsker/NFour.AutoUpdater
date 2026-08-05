using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FourSaas.AutoUpdater.Client;

/// Obtains stable filesystem identity for the parent-identity barrier.  Size and mtime are
/// observations, not identity: an attacker can replace a directory with another directory while
/// preserving both values.
internal static class FileIdentityProvider
{
    public static bool TryGet(string path, out FileIdentity identity)
    {
        identity = default;
        if (OperatingSystem.IsWindows())
        {
            using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return false;
            identity = new FileIdentity($"{info.VolumeSerialNumber}:{((ulong)info.FileIndexHigh << 32) | info.FileIndexLow}");
            return true;
        }
        if (LStat(path, out var value) != 0) return false;
        identity = new FileIdentity($"{value.Device}:{value.Inode}");
        return true;
    }

    public static bool TryGet(SafeFileHandle handle, out FileIdentity identity)
    {
        identity = default;
        if (handle.IsInvalid) return false;
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var info)) return false;
            identity = new FileIdentity($"{info.VolumeSerialNumber}:{((ulong)info.FileIndexHigh << 32) | info.FileIndexLow}");
            return true;
        }
        var descriptor = handle.DangerousGetHandle().ToInt32();
        if (fstat(descriptor, out var value) != 0) return false;
        identity = new FileIdentity($"{value.Device}:{value.Inode}");
        return true;
    }

    public static bool TryGetDevice(string path, out ulong device)
    {
        device = 0;
        if (OperatingSystem.IsWindows())
        {
            using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return false;
            device = info.VolumeSerialNumber;
            return true;
        }
        if (LStat(path, out var value) != 0) return false;
        device = value.Device;
        return true;
    }

    private static int LStat(string path, out UnixStat value)
    {
        value = default;
        try { return lstat(path, out value); }
        catch (DllNotFoundException) { return -1; }
        catch (EntryPointNotFoundException) { return -1; }
    }

    [DllImport("libc", EntryPoint = "lstat", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int lstat(string path, out UnixStat value);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int fstat(int descriptor, out UnixStat value);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);
    [StructLayout(LayoutKind.Sequential)] private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    // Linux and the BSD-derived systems used by this client expose the same identity prefix
    // (device and inode) in their 64-bit stat layout.  The remaining fields keep the native
    // buffer large enough for lstat to fill without truncation.
    [StructLayout(LayoutKind.Sequential)]
    private struct UnixStat
    {
        public ulong Device;
        public ulong Inode;
        public ulong LinkCount;
        public uint Mode;
        public uint UserId;
        public uint GroupId;
        public uint Padding;
        public ulong DeviceType;
        public long Size;
        public long BlockSize;
        public long Blocks;
        public Timespec Access;
        public Timespec Modify;
        public Timespec Change;
        public long Reserved1;
        public long Reserved2;
        public long Reserved3;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Timespec { public long Seconds; public long Nanoseconds; }
}
