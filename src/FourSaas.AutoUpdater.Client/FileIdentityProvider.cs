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
        if (!TryStat(path, out var device, out var inode)) return false;
        identity = new FileIdentity($"{device}:{inode}");
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
        if (!TryStatHandle(descriptor, out var device, out var inode)) return false;
        identity = new FileIdentity($"{device}:{inode}");
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
        return TryStat(path, out device, out _);
    }

    /// <summary>
    /// Device/inode pair, read through whichever native stat ABI this kernel actually uses.
    /// Returns false when the identity could not be obtained; callers treat that as
    /// identity-unavailable and fail the barrier rather than accepting a guess.
    /// </summary>
    private static bool TryStat(string path, out ulong device, out ulong inode)
        => IsDarwin ? TryDarwinLStat(path, out device, out inode) : TryLinuxLStat(path, out device, out inode);

    private static bool TryStatHandle(int descriptor, out ulong device, out ulong inode)
        => IsDarwin ? TryDarwinFStat(descriptor, out device, out inode) : TryLinuxFStat(descriptor, out device, out inode);

    private static readonly bool IsDarwin = OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst();

    // Darwin exports the 64-bit-inode stat family under a decorated name on x86_64.  On
    // arm64 the undecorated symbols are already the 64-bit variants.
    private static readonly bool DarwinNeedsInode64Suffix = RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private static bool TryLinuxLStat(string path, out ulong device, out ulong inode)
    {
        device = inode = 0;
        try
        {
            if (linux_lstat(path, out var value) != 0) return false;
            device = value.Device; inode = value.Inode; return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static bool TryLinuxFStat(int descriptor, out ulong device, out ulong inode)
    {
        device = inode = 0;
        try
        {
            if (linux_fstat(descriptor, out var value) != 0) return false;
            device = value.Device; inode = value.Inode; return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static bool TryDarwinLStat(string path, out ulong device, out ulong inode)
    {
        device = inode = 0;
        try
        {
            DarwinStat value;
            var result = DarwinNeedsInode64Suffix ? darwin_lstat_inode64(path, out value) : darwin_lstat(path, out value);
            if (result != 0) return false;
            device = unchecked((ulong)(uint)value.Device); inode = value.Inode; return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static bool TryDarwinFStat(int descriptor, out ulong device, out ulong inode)
    {
        device = inode = 0;
        try
        {
            DarwinStat value;
            var result = DarwinNeedsInode64Suffix ? darwin_fstat_inode64(descriptor, out value) : darwin_fstat(descriptor, out value);
            if (result != 0) return false;
            device = unchecked((ulong)(uint)value.Device); inode = value.Inode; return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    [DllImport("libc", EntryPoint = "lstat", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int linux_lstat(string path, out LinuxStat value);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int linux_fstat(int descriptor, out LinuxStat value);

    [DllImport("libc", EntryPoint = "lstat$INODE64", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int darwin_lstat_inode64(string path, out DarwinStat value);
    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int darwin_fstat_inode64(int descriptor, out DarwinStat value);
    [DllImport("libc", EntryPoint = "lstat", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int darwin_lstat(string path, out DarwinStat value);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int darwin_fstat(int descriptor, out DarwinStat value);

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

    // Linux x86-64/arm64 `struct stat`.  Note this layout is NOT shared with Darwin: there
    // st_dev is a 32-bit signed value followed by 16-bit st_mode and st_nlink, so reading a
    // Darwin buffer through this struct yields a device number spliced together from three
    // unrelated fields.  Each kernel gets its own declaration.
    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStat
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

    // Darwin `struct stat` under __DARWIN_64_BIT_INO_T (the only variant .NET runs against).
    // The trailing fields are declared so the native write cannot overrun the managed buffer.
    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinStat
    {
        public int Device;
        public ushort Mode;
        public ushort LinkCount;
        public ulong Inode;
        public uint UserId;
        public uint GroupId;
        public int DeviceType;
        public Timespec Access;
        public Timespec Modify;
        public Timespec Change;
        public Timespec Birth;
        public long Size;
        public long Blocks;
        public int BlockSize;
        public uint Flags;
        public uint Generation;
        public int Reserved;
        public long Spare1;
        public long Spare2;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Timespec { public long Seconds; public long Nanoseconds; }
}
