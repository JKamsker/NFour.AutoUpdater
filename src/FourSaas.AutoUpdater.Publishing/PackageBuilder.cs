namespace FourSaas.AutoUpdater.Publishing;

public sealed record PublishedPackage(PackageManifest Manifest, byte[] ManifestBytes, ImmutableDictionary<VirtualPath, ContentHash> Blobs);

public sealed class PackageBuilder
{
    public async ValueTask<PublishedPackage> BuildAsync(SlicedPackage package, PackageVersion version, RepositoryLayout layout, IWritableObjectStore destination, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default, PackageBuildOptions? options = null)
    {
        timeProvider ??= TimeProvider.System;
        options ??= new PackageBuildOptions();
        PublishHashCache? hashCache = options.HashCachePath is null ? null : await PublishHashCache.LoadAsync(options.HashCachePath, cancellationToken).ConfigureAwait(false);
        var files = new List<PackageFileEntry>(package.Files.Length);
        var blobs = ImmutableDictionary.CreateBuilder<VirtualPath, ContentHash>();
        var shardCount = NextPowerOfTwo(Math.Max(1, (package.Files.Length + 4095) / 4096));
        var shardRoot = Path.Combine(Path.GetTempPath(), "4sup-file-table-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(shardRoot);
        var shardPaths = Enumerable.Range(0, shardCount).Select(index => Path.Combine(shardRoot, $"{index:D8}.jsonl")).ToArray();
        var shardStreams = shardPaths.Select(path => (FileStream?)new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan)).ToArray();
        var shardCounts = new int[shardCount];
        try
        {
            foreach (var sliced in package.Files.OrderBy(x => x.Destination.Value, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hash = sliced.Kind == FileEntryKind.Directory ? ContentHash.Compute([]) : default;
                ContentHash? md5 = null;
                var length = 0L;
                if (sliced.Kind == FileEntryKind.File)
                {
                    var info = new FileInfo(sliced.SourcePath);
                    (hash, md5) = hashCache is not null && hashCache.TryGet(sliced.SourcePath, info, options.RehashAll || options.ReleaseSigningBuild, out var cached)
                        ? cached
                        : await ComputeAndCacheAsync(sliced.SourcePath, info, hashCache, cancellationToken).ConfigureAwait(false);
                    await using var source = new FileStream(sliced.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    length = source.Length;
                    blobs[sliced.Destination] = hash;
                    var probe = new PackageFileEntry { Path = sliced.Destination, Content = hash, Md5 = md5, Size = length, Policy = sliced.Policy, Kind = sliced.Kind, Mode = sliced.Mode };
                    foreach (var validator in options.Validators.Where(x => x.CanValidate(probe)))
                    {
                        source.Position = 0;
                        var result = await validator.ValidateAsync(probe, source, cancellationToken).ConfigureAwait(false);
                        if (!result.IsValid) throw new InvalidDataException(result.Error ?? $"Content validation failed for '{probe.Path}'.");
                    }
                    source.Position = 0;
                    await PutOnceAsync(destination, layout.Blob(hash), source, hash, length, cancellationToken).ConfigureAwait(false);
                }
                var entry = new PackageFileEntry { Path = sliced.Destination, Content = hash, Md5 = md5, Size = length, Policy = sliced.Policy, Kind = sliced.Kind, Mode = sliced.Mode };
                var row = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new FileRow(entry.Path.Value, sliced.Kind == FileEntryKind.File ? entry.Content.ToString() : null, entry.Size, entry.Md5?.ToString(), entry.Policy == FileInstallPolicy.Replace ? null : entry.Policy, entry.Kind == FileEntryKind.File ? null : entry.Kind, entry.Mode), RepositoryJson.Options) + "\n");
                var shard = FileTableSharding.GetShardIndex(entry.Path, shardCount);
                await shardStreams[shard]!.WriteAsync(row.AsMemory(), cancellationToken).ConfigureAwait(false);
                shardCounts[shard]++;
                files.Add(entry);
            }

            foreach (var stream in shardStreams) { await stream!.FlushAsync(cancellationToken).ConfigureAwait(false); stream.Dispose(); }
            var shardRefs = ImmutableArray.CreateBuilder<FileTableShardRef>();
            for (var index = 0; index < shardCount; index++)
            {
                await using var shard = new FileStream(shardPaths[index], FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var digest = await ContentHash.ComputeAsync(shard, HashAlgorithmId.Sha256, cancellationToken).ConfigureAwait(false);
                shard.Position = 0;
                var size = shard.Length;
                await PutOnceAsync(destination, layout.Blob(digest), shard, digest, size, cancellationToken).ConfigureAwait(false);
                shardRefs.Add(new FileTableShardRef { Index = index, Digest = digest, Count = shardCounts[index], Size = size });
            }
            var table = new FileTableRef { Format = "jsonl/v1", ShardCount = shardCount, Shards = shardRefs.ToImmutable(), Digest = FileTableSharding.ComputeTableDigest(shardRefs) };
            var downloadSize = files.Where(x => x.Kind == FileEntryKind.File).GroupBy(x => x.Content).Sum(x => x.First().Size);
            var manifest = new PackageManifest { SchemaVersion = 1, Id = package.Id, Version = version, Sequence = version.Sequence, Kind = PackageKind.Content, CreatedAt = timeProvider.GetUtcNow(), Requires = package.Requires, PathPrefixes = files.Select(x => Prefix(x.Path.Value)).Distinct(StringComparer.Ordinal).ToImmutableArray(), FileTable = table, FileCount = files.Count, InstallSize = files.Sum(x => x.Size), DownloadSize = downloadSize };
            var manifestBytes = RepositoryJson.SerializeManifest(manifest);
            await using var manifestStream = new MemoryStream(manifestBytes, writable: false);
            await PutOnceAsync(destination, layout.Package(package.Id, version), manifestStream, ContentHash.Compute(manifestBytes), manifestBytes.LongLength, cancellationToken).ConfigureAwait(false);
            if (hashCache is not null && options.HashCachePath is not null) await hashCache.SaveAsync(options.HashCachePath, cancellationToken).ConfigureAwait(false);
            return new PublishedPackage(manifest, manifestBytes, blobs.ToImmutable());
        }
        finally
        {
            foreach (var stream in shardStreams) stream?.Dispose();
            try { if (Directory.Exists(shardRoot)) Directory.Delete(shardRoot, recursive: true); } catch (IOException) { }
        }
    }

    private static async ValueTask<(ContentHash Sha256, ContentHash Md5)> ComputeAndCacheAsync(string path, FileInfo info, PublishHashCache? cache, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digests = await ContentHash.ComputeSha256AndMd5Async(source, cancellationToken).ConfigureAwait(false);
        cache?.Set(path, info, digests.Sha256, digests.Md5);
        return digests;
    }

    private static int NextPowerOfTwo(int value) { var result = 1; while (result < value) result <<= 1; return result; }
    private static string Prefix(string path) { var slash = path.IndexOf('/'); return slash < 0 ? path : path[..(slash + 1)]; }
    private static async ValueTask PutOnceAsync(IWritableObjectStore destination, ObjectKey key, Stream content, ContentHash expected, long length, CancellationToken cancellationToken)
    {
        if (key.Value.StartsWith("blobs/", StringComparison.Ordinal) && destination is IContentAddressedWriteStore addressed)
        {
            if (await addressed.PutIfAbsentAsync(key, expected, content, length, cancellationToken).ConfigureAwait(false)) return;
            var existingAddressed = await destination.OpenAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException($"CAS object '{expected}' disappeared after an existence race.");
            await using (existingAddressed.ConfigureAwait(false))
            {
                await using var bytes = new MemoryStream();
                await existingAddressed.Content.CopyToAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (ContentHash.Compute(bytes.ToArray(), expected.Algorithm) != expected) throw new CryptographicException($"Existing CAS object '{key}' failed verification.");
            }
            return;
        }
        if (destination is IConditionalWriteStore conditional)
        {
            if (await conditional.PutIfAbsentAsync(key, content, length, cancellationToken).ConfigureAwait(false)) return;
            var existing = await destination.OpenAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException($"CAS object '{expected}' disappeared after an existence race.");
            await using (existing.ConfigureAwait(false)) { await using var bytes = new MemoryStream(); await existing.Content.CopyToAsync(bytes, cancellationToken).ConfigureAwait(false); if (ContentHash.Compute(bytes.ToArray(), expected.Algorithm) != expected) throw new CryptographicException($"Existing CAS object '{key}' failed verification."); }
            return;
        }
        var existingHead = await destination.HeadAsync(key, cancellationToken).ConfigureAwait(false);
        if (existingHead is not null)
        {
            var existing = await destination.OpenAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException($"Immutable object '{key}' disappeared during verification.");
            await using (existing.ConfigureAwait(false))
            {
                await using var bytes = new MemoryStream();
                await existing.Content.CopyToAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (ContentHash.Compute(bytes.ToArray(), expected.Algorithm) != expected) throw new CryptographicException($"Existing immutable object '{key}' failed verification.");
            }
            return;
        }
        await destination.PutAsync(key, content, length, cancellationToken).ConfigureAwait(false);
    }
    private sealed record FileRow([property: JsonPropertyName("p")] string Path, [property: JsonPropertyName("h")] string? Hash, [property: JsonPropertyName("s")] long Size, [property: JsonPropertyName("m")] string? Md5, [property: JsonPropertyName("pol")] FileInstallPolicy? Policy, [property: JsonPropertyName("k")] FileEntryKind? Kind, [property: JsonPropertyName("mode")] string? Mode);
}
