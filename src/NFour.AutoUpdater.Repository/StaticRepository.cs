using System.IO.Hashing;

namespace NFour.AutoUpdater.Repository;

public sealed class StaticRepository : IPackageRepository, IManifestDigestRepository, IExactManifestRepository, IAsyncDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
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

    public async ValueTask<byte[]?> GetManifestBytesAsync(PackageId id, PackageVersion version, CancellationToken cancellationToken = default)
    {
        var result = await _store.OpenAsync(Layout.Package(id, version), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result is null) return null;
        await using (result.ConfigureAwait(false))
        {
            var bytes = await ReadAllAsync(result.Content, cancellationToken).ConfigureAwait(false);
            var manifest = RepositoryJson.DeserializeManifest(bytes);
            if (manifest.Id != id || !string.Equals(manifest.Version.Label, version.Label, StringComparison.Ordinal) || manifest.Sequence != version.Sequence)
                throw new InvalidDataException($"Manifest identity does not match requested package '{id}@{version}'.");
            return bytes;
        }
    }

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
            if (manifest.Id != id || !string.Equals(manifest.Version.Label, version.Label, StringComparison.Ordinal) || manifest.Sequence != version.Sequence)
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
        var seenFoldedPaths = new Dictionary<string, VirtualPath>(StringComparer.Ordinal);
        var totalCount = 0;
        foreach (var shard in manifest.FileTable.Shards.OrderBy(static x => x.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _store.OpenAsync(Layout.Blob(shard.Digest), cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Missing file-table shard {shard.Digest}.");
            await using (result.ConfigureAwait(false))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using var hashing = new HashingReadStream(result.Content, hash);
                using var reader = new StreamReader(hashing, StrictUtf8, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);
                var lines = new BoundedLineReader(reader, MaximumFileTableLineChars);
                var shardCount = 0;
                string? previousPath = null;
                while (await lines.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) throw new FormatException($"File-table shard {shard.Index} contains a blank JSONL line.");
                    // File-table rows are derived Class-D data.  Forward-compatible
                    // readers ignore fields introduced by newer publishers while
                    // retaining strict validation of the fields used below.
                    var dto = RepositoryJson.Deserialize<FileEntryDocument>(StrictUtf8.GetBytes(line));
                    if (!VirtualPath.TryCreate(dto.Path, out var path, out var error)) throw new FormatException(error);
                    if (!seenPaths.Add(path)) throw new InvalidDataException($"File table contains duplicate path '{path}'.");
                    if (seenFoldedPaths.TryGetValue(path.FoldedKey, out var folded) && folded != path) throw new InvalidDataException($"File table contains a case-only path collision between '{folded}' and '{path}'.");
                    seenFoldedPaths[path.FoldedKey] = path;
                    if (FileTableSharding.GetShardIndex(path, manifest.FileTable.ShardCount) != shard.Index) throw new InvalidDataException($"File-table entry '{path}' is in the wrong shard.");
                    if (previousPath is not null && StringComparer.Ordinal.Compare(previousPath, path.Value) >= 0) throw new InvalidDataException($"File-table shard {shard.Index} is not sorted by path.");
                    previousPath = path.Value;
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
                if (hashing.BytesRead != shard.Size) throw new InvalidDataException($"File-table shard {shard.Index} size does not match its declaration.");
                if (new ContentHash(HashAlgorithmId.Sha256, hash.GetHashAndReset()) != shard.Digest) throw new InvalidDataException($"File-table shard {shard.Digest} failed its digest check.");
                if (shardCount != shard.Count) throw new InvalidDataException($"File-table shard {shard.Index} count does not match its declaration.");
            }
        }
        if (totalCount != manifest.FileCount) throw new InvalidDataException("File-table entry count does not match the manifest.");
    }

    public ValueTask DisposeAsync() => _store.DisposeAsync();

    /// <summary>Largest single file-table row this reader will materialise.</summary>
    private const int MaximumFileTableLineChars = 64 * 1024;
    private static async ValueTask<byte[]> ReadAllAsync(Stream source, CancellationToken cancellationToken) { using var target = new MemoryStream(); await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false); return target.ToArray(); }

    private sealed class HashingReadStream(Stream inner, IncrementalHash hash) : Stream
    {
        public long BytesRead { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, count); Append(buffer.AsSpan(offset, read)); return read; }
        public override int Read(Span<byte> buffer) { var read = inner.Read(buffer); Append(buffer[..read]); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); Append(buffer.Span[..read]); return read; }
        private void Append(ReadOnlySpan<byte> bytes) { if (bytes.IsEmpty) return; hash.AppendData(bytes); BytesRead += bytes.Length; }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() { inner.Dispose(); return ValueTask.CompletedTask; }
        public override bool CanRead => inner.CanRead; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException(); public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new NotSupportedException(); public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

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
    /// <summary>
    /// Validates a declared file mode at ingestion.
    ///
    /// The grammar is an allowlist, and setuid/setgid/sticky are refused outright: a package
    /// able to request them would be requesting privilege escalation on every machine that
    /// installs it. Rejecting here means no such mode can reach a client at all.
    /// </summary>
    private static void ValidateMode(string? mode, FileInstallPolicy policy)
    {
        if (mode is null) return;
        if (!PosixFileMode.TryParse(mode, out _, out var error)) throw new FormatException(error);
        if (policy != FileInstallPolicy.Executable) throw new FormatException("A file mode is only valid with executable policy.");
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
        foreach (var shard in shards.OrderBy(static x => x.Index)) stream.Write(shard.Digest.Span);
        return ContentHash.Compute(stream.ToArray());
    }
}
