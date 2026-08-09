using NFour.AutoUpdater.Core;

namespace NFour.AutoUpdater.Publishing;

public sealed record ContentValidationResult(bool IsValid, string? Error = null)
{
    public static ContentValidationResult Success { get; } = new(true);
    public static ContentValidationResult Failure(string error) => new(false, error);
}

public interface IContentValidator
{
    bool CanValidate(PackageFileEntry entry);
    ValueTask<ContentValidationResult> ValidateAsync(PackageFileEntry entry, Stream content, CancellationToken cancellationToken = default);
}

public sealed class CompositeContentValidator(IEnumerable<IContentValidator> validators) : IContentValidator
{
    private readonly ImmutableArray<IContentValidator> _validators = validators.ToImmutableArray();
    public bool CanValidate(PackageFileEntry entry) => _validators.Any(x => x.CanValidate(entry));
    public async ValueTask<ContentValidationResult> ValidateAsync(PackageFileEntry entry, Stream content, CancellationToken cancellationToken = default)
    {
        foreach (var validator in _validators.Where(x => x.CanValidate(entry)))
        {
            if (content.CanSeek) content.Position = 0;
            var result = await validator.ValidateAsync(entry, content, cancellationToken).ConfigureAwait(false);
            if (!result.IsValid) return result;
        }
        return ContentValidationResult.Success;
    }
}

public sealed class JsonContentValidator : IContentValidator
{
    public bool CanValidate(PackageFileEntry entry) => entry.Kind == FileEntryKind.File && entry.Path.Value.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    public async ValueTask<ContentValidationResult> ValidateAsync(PackageFileEntry entry, Stream content, CancellationToken cancellationToken = default)
    {
        try { using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false); return ContentValidationResult.Success; }
        catch (JsonException ex) { return ContentValidationResult.Failure($"JSON file '{entry.Path}' is invalid: {ex.Message}"); }
    }
}

public sealed class PeHeaderValidator : IContentValidator
{
    public bool CanValidate(PackageFileEntry entry) => entry.Kind == FileEntryKind.File && entry.Path.Value.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) && (entry.Path.Value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || entry.Path.Value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
    public async ValueTask<ContentValidationResult> ValidateAsync(PackageFileEntry entry, Stream content, CancellationToken cancellationToken = default)
    {
        var header = new byte[2];
        var read = await content.ReadAsync(header, cancellationToken).ConfigureAwait(false);
        return read == 2 && header[0] == (byte)'M' && header[1] == (byte)'Z' ? ContentValidationResult.Success : ContentValidationResult.Failure($"PE file '{entry.Path}' does not start with an MZ header.");
    }
}

public sealed record PackageBuildOptions
{
    public string? HashCachePath { get; init; }
    public bool RehashAll { get; init; }
    public bool ReleaseSigningBuild { get; init; }
    public ImmutableArray<IContentValidator> Validators { get; init; } = [];
}

public sealed record PublishHashCacheEntry
{
    public required string Path { get; init; }
    public required long Length { get; init; }
    public required long LastWriteTimeUtcTicks { get; init; }
    public required long ChangeTimeUtcTicks { get; init; }
    public required string FileIdentity { get; init; }
    public required ContentHash Sha256 { get; init; }
    public required ContentHash Md5 { get; init; }
}

/// <summary>Persisted speed cache. Cache hits are never used for release-signing builds.</summary>
public sealed class PublishHashCache
{
    private readonly Dictionary<string, PublishHashCacheEntry> _entries = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Options = new(RepositoryJson.Options) { WriteIndented = true };

    public static async ValueTask<PublishHashCache> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var cache = new PublishHashCache();
        if (!File.Exists(path)) return cache;
        try
        {
            await using var stream = File.OpenRead(path);
            var entries = await JsonSerializer.DeserializeAsync<Dictionary<string, PublishHashCacheEntry>>(stream, Options, cancellationToken).ConfigureAwait(false);
            if (entries is not null) foreach (var pair in entries) cache._entries[pair.Key] = pair.Value;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return cache;
    }

    public async ValueTask SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous)) await JsonSerializer.SerializeAsync(stream, _entries, Options, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    public bool TryGet(string path, FileInfo info, bool rehashAll, out (ContentHash Sha256, ContentHash Md5) digests)
    {
        digests = default;
        if (rehashAll || !_entries.TryGetValue(path, out var entry)) return false;
        var metadata = FileMetadata(path, info);
        if (entry.Length != info.Length || entry.LastWriteTimeUtcTicks != info.LastWriteTimeUtc.Ticks || entry.ChangeTimeUtcTicks != metadata.ChangeTimeUtcTicks || !string.Equals(entry.FileIdentity, metadata.Identity, StringComparison.Ordinal)) return false;
        digests = (entry.Sha256, entry.Md5);
        return true;
    }

    public void Set(string path, FileInfo info, ContentHash sha256, ContentHash md5)
    {
        var metadata = FileMetadata(path, info);
        _entries[path] = new PublishHashCacheEntry { Path = path, Length = info.Length, LastWriteTimeUtcTicks = info.LastWriteTimeUtc.Ticks, ChangeTimeUtcTicks = metadata.ChangeTimeUtcTicks, FileIdentity = metadata.Identity, Sha256 = sha256, Md5 = md5 };
    }

    private static (string Identity, long ChangeTimeUtcTicks) FileMetadata(string path, FileInfo info)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (OperatingSystem.IsWindows() && GetFileInformationByHandle(stream.SafeFileHandle, out var windows)) return ($"{windows.VolumeSerialNumber}:{((ulong)windows.FileIndexHigh << 32) | windows.FileIndexLow}", info.LastWriteTimeUtc.Ticks);
            if (!OperatingSystem.IsWindows() && fstat(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), out var unix) == 0) return ($"{unix.Device}:{unix.Inode}", DateTime.UnixEpoch.Ticks + checked(unix.ChangeSeconds * TimeSpan.TicksPerSecond) + unix.ChangeNanoseconds / 100);
        }
        catch (IOException) { }
        return (Path.GetFullPath(path), info.CreationTimeUtc.Ticks);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out ByHandleFileInformation information);
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)] private static extern int fstat(int descriptor, out UnixStat value);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct ByHandleFileInformation { public uint FileAttributes; public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime; public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime; public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime; public uint VolumeSerialNumber; public uint FileSizeHigh; public uint FileSizeLow; public uint NumberOfLinks; public uint FileIndexHigh; public uint FileIndexLow; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct UnixStat { public ulong Device; public ulong Inode; public ulong LinkCount; public uint Mode; public uint UserId; public uint GroupId; public uint Padding; public ulong DeviceType; public long Size; public long BlockSize; public long Blocks; public long AccessSeconds; public long AccessNanoseconds; public long ModifySeconds; public long ModifyNanoseconds; public long ChangeSeconds; public long ChangeNanoseconds; public long Reserved1; public long Reserved2; public long Reserved3; }
}
