using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace NFour.AutoUpdater.Client;

/// <summary>Represents a failed safety precondition immediately before apply.</summary><param name="message">The failure explanation.</param>
public sealed class ApplyPreconditionException(string message) : Exception(message);

/// <summary>Represents failure to acquire exclusive ownership of an install root.</summary><param name="message">The failure explanation.</param>
public sealed class InstallConcurrencyException(string message) : Exception(message);

/// <summary>Defines state and resource requirements revalidated immediately before apply.</summary>
public sealed record ApplyPreconditions
{
    /// <summary>Gets an explicit minimum free-space requirement.</summary>
    public long? MinimumFreeSpaceBytes { get; init; }
    /// <summary>Gets the currently installed release sequence.</summary>
    public long? CurrentInstalledReleaseSequence { get; init; }
    /// <summary>Gets the minimum release sequence from which the target is reachable.</summary>
    public long? MinimumInstalledReleaseSequence { get; init; }
    /// <summary>Gets the running client version.</summary>
    public Version? ClientVersion { get; init; }
    /// <summary>Gets the minimum client version required by the target.</summary>
    public Version? MinimumClientVersion { get; init; }

    /// <summary>Validates root identity, topology, version, privilege, space, and path limits.</summary>
    public void Validate(string installRoot, InstallPlan plan)
    {
        var root = Path.GetFullPath(installRoot);
        if (plan.RootIdentity is { } expectedRoot && (!FileIdentityProvider.TryGet(root, out var actualRoot) || actualRoot != expectedRoot))
            throw new ApplyPreconditionException("The install root identity changed after planning.");
        var staging = Path.GetFullPath(Path.Combine(root, ".4sup", "staging"));
        if (!IsOnSameVolume(root, staging)) throw new ApplyPreconditionException("The staging directory must be on the same volume as the install root.");

        if (MinimumInstalledReleaseSequence is { } minimumRelease &&
            (CurrentInstalledReleaseSequence is not { } currentRelease || currentRelease < minimumRelease))
            throw new ApplyPreconditionException($"The installed release sequence does not satisfy minimumInstalledRelease {minimumRelease}.");

        if (MinimumClientVersion is { } minimumClient &&
            (ClientVersion is not { } client || client < minimumClient))
            throw new ApplyPreconditionException($"Client version {ClientVersion?.ToString() ?? "unknown"} is below minimumClientVersion {minimumClient}.");

        var requiredByVolume = plan.PeakFreeSpaceRequiredByVolume;
        if (MinimumFreeSpaceBytes is { } minimumFreeSpace)
        {
            var rootVolume = Path.GetPathRoot(root) ?? root;
            requiredByVolume = requiredByVolume.SetItem(rootVolume, Math.Max(requiredByVolume.TryGetValue(rootVolume, out var existing) ? existing : 0, minimumFreeSpace));
        }

        if (IsElevated() && Directory.Exists(root))
        {
            if (OperatingSystem.IsWindows() && IsWindowsUserWritable(root))
                throw new ApplyPreconditionException("An elevated updater will not mutate a user-writable install root.");
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(root) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw new ApplyPreconditionException("An elevated updater will not mutate a group- or world-writable install root.");
        }

        foreach (var requirement in requiredByVolume)
        {
            var volume = Path.GetPathRoot(Path.GetFullPath(string.IsNullOrWhiteSpace(requirement.Key) ? root : requirement.Key));
            if (string.IsNullOrWhiteSpace(volume)) throw new ApplyPreconditionException($"Unable to determine the volume for '{requirement.Key}'.");
            DriveInfo drive;
            try { drive = new DriveInfo(volume); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            { throw new ApplyPreconditionException($"Unable to inspect free space for '{volume}': {ex.Message}"); }
            if (drive.AvailableFreeSpace < requirement.Value)
                throw new ApplyPreconditionException($"Volume '{volume}' has {drive.AvailableFreeSpace} bytes free, but {requirement.Value} are required.");
        }

        foreach (var operation in plan.Operations)
        {
            var candidate = Path.GetFullPath(Path.Combine(root, operation.Path.Value.Replace('/', Path.DirectorySeparatorChar)));
            if (OperatingSystem.IsWindows() && candidate.Length > 240)
                throw new ApplyPreconditionException($"Managed path '{operation.Path}' is too long for a safe apply.");
        }
    }

    private static bool IsOnSameVolume(string left, string right)
    {
        if (!OperatingSystem.IsWindows() && FileIdentityProvider.TryGetDevice(left, out var leftDevice) && FileIdentityProvider.TryGetDevice(right, out var rightDevice))
            return leftDevice == rightDevice;
        var leftRoot = Path.GetPathRoot(left);
        var rightRoot = Path.GetPathRoot(right);
        return !string.IsNullOrEmpty(leftRoot) && string.Equals(leftRoot, rightRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool IsElevated()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (PlatformNotSupportedException) { return false; }
        }
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return false;
        try { return GetEffectiveUserId() == 0; } catch (DllNotFoundException) { return false; } catch (EntryPointNotFoundException) { return false; }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsUserWritable(string root)
    {
        try
        {
            var security = new DirectoryInfo(root).GetAccessControl(AccessControlSections.Access);
            var broadPrincipals = new HashSet<SecurityIdentifier>
            {
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null)
            };
            const FileSystemRights writeRights = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.Modify | FileSystemRights.FullControl;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Allow && broadPrincipals.Contains((SecurityIdentifier)rule.IdentityReference) && (rule.FileSystemRights & writeRights) != 0)
                    return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        {
            // An elevated install must fail closed if its ACL cannot be inspected.
            return true;
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}

/// <summary>Acquires exclusive ownership of an install root for apply.</summary>
public interface IInstallLockProvider
{
    /// <summary>Acquires an install lock or throws when another owner holds it.</summary>
    ValueTask<IInstallLockLease> AcquireAsync(string installRoot, CancellationToken cancellationToken = default);
}

/// <summary>Represents exclusive ownership of an install root.</summary>
public interface IInstallLockLease : IAsyncDisposable
{
    /// <summary>Gets diagnostic text identifying the lock owner.</summary>
    string Owner { get; }
}

/// <summary>Provides a durable OS-level advisory lock with diagnostic owner metadata.</summary>
public sealed class FileInstallLockProvider : IInstallLockProvider
{
    /// <inheritdoc />
    public ValueTask<IInstallLockLease> AcquireAsync(string installRoot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var metadata = Path.Combine(Path.GetFullPath(installRoot), ".4sup");
        Directory.CreateDirectory(metadata);
        var path = Path.Combine(metadata, "apply.lock");
        var owner = $"pid={Environment.ProcessId};host={Environment.MachineName};started={DateTimeOffset.UtcNow:O}";
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 256, FileOptions.WriteThrough);
#pragma warning disable CA1416
            stream.Lock(0, 1);
#pragma warning restore CA1416
            stream.SetLength(0);
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                writer.Write(owner);
                writer.Flush();
            }
            stream.Flush(flushToDisk: true);
            return ValueTask.FromResult<IInstallLockLease>(new FileInstallLockLease(stream, owner, null));
        }
        catch (PlatformNotSupportedException) when (OperatingSystem.IsMacOS())
        {
            stream?.Dispose();
            var mutexName = "4sup-apply-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..24];
            var mutex = new Mutex(false, mutexName);
            if (!mutex.WaitOne(0))
            {
                mutex.Dispose();
                throw new InstallConcurrencyException($"Install root '{installRoot}' is already locked by another updater process.");
            }
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 256, FileOptions.WriteThrough);
            stream.SetLength(0);
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)) { writer.Write(owner); writer.Flush(); }
            stream.Flush(flushToDisk: true);
            return ValueTask.FromResult<IInstallLockLease>(new FileInstallLockLease(stream, owner, mutex));
        }
        catch (IOException)
        {
            stream?.Dispose();
            var previousOwner = "unknown holder";
            try
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                if (!string.IsNullOrWhiteSpace(text)) previousOwner = text;
            }
            catch (Exception) { }
            throw new InstallConcurrencyException($"Install root '{installRoot}' is already locked by {previousOwner}.");
        }
    }

    private sealed class FileInstallLockLease(FileStream stream, string owner, Mutex? fallbackMutex) : IInstallLockLease
    {
        public string Owner => owner;

        public ValueTask DisposeAsync()
        {
            try
            {
#pragma warning disable CA1416
                stream.Unlock(0, 1);
#pragma warning restore CA1416
            }
            catch (IOException) { }
            catch (PlatformNotSupportedException) { }
            stream.Dispose();
            if (fallbackMutex is not null)
            {
                try { fallbackMutex.ReleaseMutex(); } catch (ApplicationException) { }
                fallbackMutex.Dispose();
            }
            return ValueTask.CompletedTask;
        }
    }
}
