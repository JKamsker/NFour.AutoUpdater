using System.IO.Hashing;

namespace FourSaas.AutoUpdater.Repository;

public sealed class StaticRepository : IPackageRepository, IManifestDigestRepository, IAsyncDisposable
{
    private readonly IReadableObjectStore _store;
    public StaticRepository(IReadableObjectStore store, RepositoryDescriptor descriptor)
    {
        _store = store;
        Descriptor = descriptor;
        Layout = new RepositoryLayout(descriptor.Layout);
    }
    public RepositoryDescriptor Descriptor { get; }
    public RepositoryLayout Layout { get; }

    public async ValueTask<PackageManifest?> GetManifestAsync(PackageId id, PackageVersion version, CancellationToken cancellationToken = default)
        => await GetManifestCoreAsync(id, version, null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<PackageManifest?> GetManifestAsync(PackageId id, PackageVersion version, ContentHash expectedDigest, CancellationToken cancellationToken = default)
        => await GetManifestCoreAsync(id, version, expectedDigest, cancellationToken).ConfigureAwait(false);

    private async ValueTask<PackageManifest?> GetManifestCoreAsync(PackageId id, PackageVersion version, ContentHash? expectedDigest, CancellationToken cancellationToken)
    {
        var result = await _store.OpenAsync(Layout.Package(id, version), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result is null) return null;
        await using (result.ConfigureAwait(false))
        {
            var bytes = await ReadAllAsync(result.Content, cancellationToken).ConfigureAwait(false);
            if (expectedDigest is { } digest && ContentHash.Compute(bytes) != digest) throw new CryptographicException($"Manifest '{id}@{version}' failed its digest check.");
            var manifest = RepositoryJson.DeserializeManifest(bytes);
            if (manifest.SchemaVersion != 1) throw new FormatException($"Unsupported package schemaVersion {manifest.SchemaVersion}.");
            if (manifest.Id != id || !string.Equals(manifest.Version.Label, version.Label, StringComparison.Ordinal) || manifest.Version.Sequence != version.Sequence)
                throw new InvalidDataException($"Manifest identity does not match requested package '{id}@{version}'.");
            return manifest;
        }
    }

    public async IAsyncEnumerable<PackageFileEntry> ReadFileTableAsync(PackageManifest manifest, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!string.Equals(manifest.FileTable.Format, "jsonl/v1", StringComparison.Ordinal)) throw new FormatException($"Unsupported file table format '{manifest.FileTable.Format}'.");
        if (manifest.FileTable.ShardCount < 1 || (manifest.FileTable.ShardCount & (manifest.FileTable.ShardCount - 1)) != 0) throw new FormatException("File table shardCount must be a positive power of two.");
        if (manifest.FileTable.Shards.Length != manifest.FileTable.ShardCount || manifest.FileTable.Shards.Select(static x => x.Index).OrderBy(static x => x).SequenceEqual(Enumerable.Range(0, manifest.FileTable.ShardCount)) is false)
            throw new FormatException("File table must contain exactly one shard reference for every shard index.");
        if (manifest.FileTable.Shards.Any(static x => x.Digest.Algorithm != HashAlgorithmId.Sha256)) throw new FormatException("File-table shard digests must use sha256.");
        if (FileTableSharding.ComputeTableDigest(manifest.FileTable.Shards) != manifest.FileTable.Digest) throw new CryptographicException("File-table digest does not match its shard references.");
        var seenPaths = new HashSet<VirtualPath>();
        var totalCount = 0;
        foreach (var shard in manifest.FileTable.Shards.OrderBy(static x => x.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _store.OpenAsync(Layout.Blob(shard.Digest), cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Missing file-table shard {shard.Digest}.");
            await using (result.ConfigureAwait(false))
            {
                var bytes = await ReadAllAsync(result.Content, cancellationToken).ConfigureAwait(false);
                if (ContentHash.Compute(bytes) != shard.Digest) throw new InvalidDataException($"File-table shard {shard.Digest} failed its digest check.");
                if (bytes.LongLength != shard.Size) throw new InvalidDataException($"File-table shard {shard.Index} size does not match its declaration.");
                var shardCount = 0;
                foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var dto = RepositoryJson.Deserialize<FileEntryDocument>(Encoding.UTF8.GetBytes(line.TrimEnd('\r')));
                    if (!VirtualPath.TryCreate(dto.Path, out var path, out var error)) throw new FormatException(error);
                    if (!seenPaths.Add(path)) throw new InvalidDataException($"File table contains duplicate path '{path}'.");
                    if (dto.Size < 0) throw new FormatException("File-table entry size cannot be negative.");
                    ValidateMode(dto.Mode, dto.Policy);
                    if (dto.Kind == FileEntryKind.Directory)
                    {
                        if (dto.Size != 0 || dto.Hash is not null) throw new FormatException("Directory file-table entries must have size zero and no content hash.");
                        yield return new PackageFileEntry { Path = path, Content = ContentHash.Compute([]), Size = dto.Size, Kind = FileEntryKind.Directory, Policy = dto.Policy, Mode = dto.Mode };
                    }
                    else
                    {
                        if (dto.Hash is null) throw new FormatException("File-table file row has no content hash.");
                        var content = ContentHash.Parse(dto.Hash);
                        if (content.Algorithm != HashAlgorithmId.Sha256) throw new FormatException("File-table content hashes must use sha256.");
                        var md5 = dto.Md5 is null ? (ContentHash?)null : ContentHash.Parse(dto.Md5);
                        if (md5 is { Algorithm: not HashAlgorithmId.Md5 }) throw new FormatException("File-table change-detection hashes must use md5.");
                        yield return new PackageFileEntry { Path = path, Content = content, Size = dto.Size, Md5 = md5, Policy = dto.Policy, Kind = FileEntryKind.File, Mode = dto.Mode };
                    }
                    shardCount++;
                    totalCount++;
                }
                if (shardCount != shard.Count) throw new InvalidDataException($"File-table shard {shard.Index} count does not match its declaration.");
            }
        }
        if (totalCount != manifest.FileCount) throw new InvalidDataException("File-table entry count does not match the manifest.");
    }

    public ValueTask DisposeAsync() => _store.DisposeAsync();
    private static async ValueTask<byte[]> ReadAllAsync(Stream source, CancellationToken cancellationToken) { using var target = new MemoryStream(); await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false); return target.ToArray(); }

    private sealed record FileEntryDocument
    {
        [JsonPropertyName("p")] public required string Path { get; init; }
        [JsonPropertyName("h")] public string? Hash { get; init; }
        [JsonPropertyName("s")] public required long Size { get; init; }
        [JsonPropertyName("m")] public string? Md5 { get; init; }
        [JsonPropertyName("pol")] public FileInstallPolicy Policy { get; init; } = FileInstallPolicy.Replace;
        [JsonPropertyName("k")] public FileEntryKind Kind { get; init; } = FileEntryKind.File;
        [JsonPropertyName("mode")] public string? Mode { get; init; }
    }
    private static void ValidateMode(string? mode, FileInstallPolicy policy)
    {
        if (mode is not null && (mode.Length != 4 || mode.Any(x => x is < '0' or > '7'))) throw new FormatException("File-table mode must be a four-digit POSIX octal string.");
        if (mode is not null && policy != FileInstallPolicy.Executable) throw new FormatException("A file mode is only valid with executable policy.");
    }
}

public static class FileTableSharding
{
    public static int GetShardIndex(VirtualPath path, int shardCount)
    {
        if (shardCount < 1 || (shardCount & (shardCount - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(shardCount));
        var hash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(path.Value));
        return unchecked((int)(hash & (uint)(shardCount - 1)));
    }
    public static ContentHash ComputeTableDigest(IEnumerable<FileTableShardRef> shards)
    {
        using var stream = new MemoryStream();
        foreach (var shard in shards.OrderBy(static x => x.Index)) stream.Write(shard.Digest.Value.Span);
        return ContentHash.Compute(stream.ToArray());
    }
}
