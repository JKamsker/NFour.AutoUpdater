using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FourSaas.AutoUpdater.Client;

/// Windows handle-relative mutation adapter.  The install root and every component
/// below it are opened from a parent handle with OBJ_DONT_REPARSE and
/// FILE_OPEN_REPARSE_POINT.  Mutations use those handles; no validate-then-reopen
/// path operation is used for an install file.
internal static class WindowsSecureInstallOperations
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint FileShareDelete = 4;
    private const uint FileOpen = 1;
    private const uint FileCreate = 2;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileSynchronousIoNonalert = 0x00000020;
    private const uint FileOpenReparsePoint = 0x00000200;
    private const uint FileOpenForBackupIntent = 0x00004000;
    private const uint Win32OpenReparsePoint = 0x00200000;
    private const uint Win32BackupSemantics = 0x02000000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint ObjCaseInsensitive = 0x00000040;
    private const uint ObjDontReparse = 0x00001000;
    private const int FileRenameInformation = 10;
    private const int FileDispositionInformation = 4;
    private const int FileAttributeTagInformationClass = 35;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int StatusSuccess = 0;
    private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
    private const int StatusObjectNameCollision = unchecked((int)0xC0000035);
    private const int StatusReparsePointEncountered = unchecked((int)0xC000050B);

    public static bool TryEnsureDirectory(string root, VirtualPath path)
    {
        using var current = OpenDirectoryTree(root, path.Value.Split('/'));
        return true;
    }

    public static bool TryFileExists(string root, VirtualPath path, FileIdentity? expectedParent = null)
    {
        using var parent = OpenParentDirectory(root, path);
        VerifyParent(parent, expectedParent, path);
        using var file = OpenRelative(parent, Path.GetFileName(path.Value), GenericRead | Synchronize, FileNonDirectoryFile, FileOpen);
        if (file is null) return false;
        var attributes = QueryAttributes(file);
        if ((attributes & FileAttributeReparsePoint) != 0) throw new IOException($"Managed path is a reparse point: '{path}'.");
        return (attributes & FileAttributeDirectory) == 0;
    }

    public static bool TryReplaceFile(string root, VirtualPath path, string stagedPath, FileIdentity? expectedParent = null)
    {
        using var parent = OpenParentDirectory(root, path, createMissing: true);
        VerifyParent(parent, expectedParent, path);
        var temporaryName = ".4sup-new-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        using var temporary = OpenRelative(parent, temporaryName, GenericRead | GenericWrite | DeleteAccess | Synchronize, FileNonDirectoryFile, FileCreate, allowExistingOnCollision: false)
            ?? throw new IOException($"Unable to create temporary file for '{path}'.");
        var renamed = false;
        try
        {
            using (var output = new FileStream(temporary, FileAccess.Write, 128 * 1024, isAsync: false))
            using (var input = File.OpenRead(stagedPath))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            RenameByHandle(temporary, parent, Path.GetFileName(path.Value), replace: true);
            renamed = true;
            return true;
        }
        finally
        {
            // Delete the temporary through its already-open handle.  This remains
            // safe even if an attacker races the namespace after a failed rename.
            if (!renamed && !temporary.IsClosed && !temporary.IsInvalid)
            {
                try { MarkDeleted(temporary); } catch (IOException) { }
            }
        }
    }

    public static bool TryDeleteFile(string root, VirtualPath path, FileIdentity? expectedParent = null)
    {
        using var parent = OpenParentDirectory(root, path);
        VerifyParent(parent, expectedParent, path);
        using var file = OpenRelative(parent, Path.GetFileName(path.Value), GenericRead | DeleteAccess | Synchronize, FileNonDirectoryFile, FileOpen);
        if (file is null) return true;
        var attributes = QueryAttributes(file);
        if ((attributes & FileAttributeReparsePoint) != 0) throw new IOException($"Managed path is a reparse point: '{path}'.");
        MarkDeleted(file);
        return true;
    }

    private static SafeFileHandle OpenDirectoryTree(string root, IReadOnlyList<string> components)
    {
        var current = OpenAbsoluteDirectoryRoot(root);
        try
        {
            foreach (var component in components)
            {
                if (string.IsNullOrEmpty(component) || component == ".") continue;
                if (component is ".." || component.IndexOfAny(['/', '\\']) >= 0) throw new IOException("Invalid managed path component.");
                var next = OpenRelative(current, component, GenericRead | GenericWrite | Synchronize, FileDirectoryFile, FileOpen)
                    ?? OpenRelative(current, component, GenericRead | GenericWrite | Synchronize, FileDirectoryFile, FileCreate)
                    ?? throw new IOException($"Unable to create managed directory '{component}'.");
                EnsureDirectory(next, component);
                current.Dispose();
                current = next;
            }
            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenParentDirectory(string root, VirtualPath path, bool createMissing = false)
    {
        var parts = path.Value.Split('/');
        if (parts.Length <= 1) return OpenAbsoluteDirectoryRoot(root);
        var parentParts = parts[..^1];
        if (!createMissing) return OpenDirectoryTree(root, parentParts);

        var current = OpenAbsoluteDirectoryRoot(root);
        try
        {
            foreach (var component in parentParts)
            {
                if (component is "" or "." or ".." || component.IndexOfAny(['/', '\\']) >= 0) throw new IOException("Invalid managed path component.");
                var next = OpenRelative(current, component, GenericRead | GenericWrite | Synchronize, FileDirectoryFile, FileOpen)
                    ?? OpenRelative(current, component, GenericRead | GenericWrite | Synchronize, FileDirectoryFile, FileCreate)
                    ?? throw new IOException($"Unable to create managed directory '{component}'.");
                EnsureDirectory(next, component);
                current.Dispose();
                current = next;
            }
            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenAbsoluteDirectoryRoot(string path)
    {
        var absolute = Path.GetFullPath(path);
        var volume = Path.GetPathRoot(absolute) ?? throw new IOException($"Unable to determine the volume for '{path}'.");
        using var volumeHandle = OpenAbsolute(volume, GenericRead | GenericWrite | Synchronize, 0);
        var relative = Path.GetRelativePath(volume, absolute);
        var components = relative == "." ? [] : relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return OpenDirectoryFromHandle(volumeHandle, components);
    }

    private static SafeFileHandle OpenDirectoryFromHandle(SafeFileHandle root, IReadOnlyList<string> components)
    {
        var current = DuplicateHandle(root);
        try
        {
            foreach (var component in components)
            {
                if (component is "" or "." or ".." || component.IndexOfAny(['/', '\\']) >= 0) throw new IOException("Invalid install root component.");
                var next = OpenRelative(current, component, GenericRead | GenericWrite | Synchronize, FileDirectoryFile, FileOpen)
                    ?? throw new IOException($"Install-root component '{component}' does not exist.");
                EnsureDirectory(next, component);
                current.Dispose();
                current = next;
            }
            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenAbsolute(string path, uint access, uint options)
    {
        var handle = CreateFile(path, access, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, 3, options | Win32OpenReparsePoint | Win32BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) ThrowLastError($"open '{path}'");
        EnsureDirectory(handle, path);
        return handle;
    }

    private static SafeFileHandle? OpenRelative(SafeFileHandle parent, string name, uint access, uint options, uint disposition, bool allowExistingOnCollision = true)
    {
        var objectName = new NativeUnicodeString(name);
        try
        {
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = objectName.Pointer,
                Attributes = ObjCaseInsensitive | ObjDontReparse
            };
            var status = NtCreateFile(out var handle, access, ref attributes, out _, IntPtr.Zero, 0, FileShareRead | FileShareWrite | FileShareDelete, disposition, options | FileSynchronousIoNonalert | FileOpenReparsePoint | FileOpenForBackupIntent, IntPtr.Zero, 0);
            if (status is StatusObjectNameNotFound or StatusReparsePointEncountered)
            {
                handle.Dispose();
                if (status == StatusObjectNameNotFound) return null;
                ThrowNt($"open managed component '{name}'", status);
            }
            if (status == StatusObjectNameCollision && disposition == FileCreate && allowExistingOnCollision)
            {
                handle.Dispose();
                return OpenRelative(parent, name, access, options, FileOpen);
            }
            if (status != StatusSuccess) { handle.Dispose(); ThrowNt($"open managed component '{name}'", status); }
            return handle;
        }
        finally { objectName.Dispose(); }
    }

    private static SafeFileHandle DuplicateHandle(SafeFileHandle source)
    {
        if (!DuplicateHandle(GetCurrentProcess(), source, GetCurrentProcess(), out var duplicate, 0, false, 2)) ThrowLastError("duplicate install-root handle");
        return duplicate;
    }

    private static void EnsureDirectory(SafeFileHandle handle, string name)
    {
        var attributes = QueryAttributes(handle);
        if ((attributes & FileAttributeReparsePoint) != 0) throw new IOException($"Reparse point in managed path component '{name}'.");
        if ((attributes & FileAttributeDirectory) == 0) throw new IOException($"Managed path component '{name}' is not a directory.");
    }

    private static void VerifyParent(SafeFileHandle parent, FileIdentity? expected, VirtualPath path)
    {
        if (expected is null) return;
        if (!FileIdentityProvider.TryGet(parent, out var actual) || actual != expected.Value)
            throw new ApplyPreconditionException($"Parent directory identity changed before mutating '{path}'.");
    }

    private static uint QueryAttributes(SafeFileHandle handle)
    {
        var status = NtQueryInformationFile(handle, out _, out FileAttributeTagInformation value, (uint)Marshal.SizeOf<FileAttributeTagInformation>(), FileAttributeTagInformationClass);
        if (status != StatusSuccess) ThrowNt("query managed path attributes", status);
        return value.FileAttributes;
    }

    private static void MarkDeleted(SafeFileHandle handle)
    {
        var disposition = new FileDispositionInformationValue { DeleteFile = 1 };
        var status = NtSetInformationFile(handle, out _, ref disposition, (uint)Marshal.SizeOf<FileDispositionInformationValue>(), FileDispositionInformation);
        if (status != StatusSuccess) ThrowNt("delete managed file", status);
    }

    private static void RenameByHandle(SafeFileHandle source, SafeFileHandle parent, string name, bool replace)
    {
        var bytes = Encoding.Unicode.GetBytes(name);
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var headerSize = lengthOffset + sizeof(int);
        var buffer = Marshal.AllocHGlobal(headerSize + bytes.Length);
        try
        {
            Marshal.WriteInt32(buffer, replace ? 1 : 0);
            Marshal.WriteIntPtr(buffer, rootOffset, parent.DangerousGetHandle());
            Marshal.WriteInt32(buffer, lengthOffset, bytes.Length);
            Marshal.Copy(bytes, 0, IntPtr.Add(buffer, headerSize), bytes.Length);
            var status = NtSetInformationFileRaw(source, out _, FileRenameInformation, buffer, (uint)(headerSize + bytes.Length));
            if (status != StatusSuccess) ThrowNt("rename verified temporary file", status);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void ThrowNt(string operation, int status) => throw new IOException($"Unable to {operation}: NTSTATUS 0x{unchecked((uint)status):X8}");
    private static void ThrowLastError(string operation) => throw new IOException($"Unable to {operation}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

    private sealed class NativeUnicodeString : IDisposable
    {
        private readonly IntPtr _buffer;
        public IntPtr Pointer { get; }
        public NativeUnicodeString(string value)
        {
            _buffer = Marshal.StringToHGlobalUni(value);
            var valueStruct = new UnicodeString { Length = checked((ushort)(value.Length * 2)), MaximumLength = checked((ushort)(value.Length * 2)), Buffer = _buffer };
            Pointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(valueStruct, Pointer, false);
        }
        public void Dispose() { Marshal.FreeHGlobal(Pointer); Marshal.FreeHGlobal(_buffer); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatusBlock { public IntPtr Status; public IntPtr Information; }
    [StructLayout(LayoutKind.Sequential)] private struct FileAttributeTagInformation { public uint FileAttributes; public uint ReparseTag; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct FileDispositionInformationValue { public byte DeleteFile; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeFileHandle sourceHandle, IntPtr targetProcess, out SafeFileHandle targetHandle, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("ntdll.dll")] private static extern int NtCreateFile(out SafeFileHandle fileHandle, uint desiredAccess, ref ObjectAttributes objectAttributes, out IoStatusBlock ioStatusBlock, IntPtr allocationSize, uint fileAttributes, uint shareAccess, uint createDisposition, uint createOptions, IntPtr eaBuffer, uint eaLength);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationFile(SafeFileHandle fileHandle, out IoStatusBlock ioStatusBlock, out FileAttributeTagInformation fileInformation, uint length, int fileInformationClass);
    [DllImport("ntdll.dll", EntryPoint = "NtSetInformationFile")] private static extern int NtSetInformationFile(SafeFileHandle fileHandle, out IoStatusBlock ioStatusBlock, ref FileDispositionInformationValue fileInformation, uint length, int fileInformationClass);
    [DllImport("ntdll.dll", EntryPoint = "NtSetInformationFile")] private static extern int NtSetInformationFileRaw(SafeFileHandle fileHandle, out IoStatusBlock ioStatusBlock, int fileInformationClass, IntPtr fileInformation, uint length);
}
