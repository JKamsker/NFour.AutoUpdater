namespace NFour.AutoUpdater.Publishing;

public sealed record PublishedPackage(PackageManifest Manifest, byte[] ManifestBytes, ImmutableDictionary<VirtualPath, ContentHash> Blobs);

public sealed class PackageBuilder
{
    public async ValueTask<PublishedPackage> BuildAsync(SlicedPackage package, PackageVersion version, RepositoryLayout layout, IWritableObjectStore destination, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default, PackageBuildOptions? options = null)
    {
        timeProvider ??= TimeProvider.System;
        options ??= new PackageBuildOptions();
        PublishHashCache? hashCache = options.HashCachePath is null ? null : await PublishHashCache.LoadAsync(options.HashCachePath, cancellationToken).ConfigureAwait(false);
        // Running aggregates rather than a retained row list. The manifest needs only counts,
        // sizes, the distinct content set and the prefix set, so keeping every PackageFileEntry
        // made peak memory proportional to the file count of the package being built.
        var fileCount = 0;
        var installSize = 0L;
        var downloadSize = 0L;
        var countedContent = new HashSet<ContentHash>();
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
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
                fileCount++;
                installSize += entry.Size;
                prefixes.Add(Prefix(entry.Path.Value));
                // Download size counts each distinct blob once, however many paths share it.
                if (entry.Kind == FileEntryKind.File && countedContent.Add(entry.Content)) downloadSize += entry.Size;
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
            var manifest = new PackageManifest { SchemaVersion = 1, Id = package.Id, Version = version, Sequence = version.Sequence, Kind = PackageKind.Content, CreatedAt = timeProvider.GetUtcNow(), Requires = package.Requires, PathPrefixes = prefixes.ToImmutableArray(), FileTable = table, FileCount = fileCount, InstallSize = installSize, DownloadSize = downloadSize };
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
            await VerifyExistingAsync(destination, key, expected, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (destination is IConditionalWriteStore conditional)
        {
            if (await conditional.PutIfAbsentAsync(key, content, length, cancellationToken).ConfigureAwait(false)) return;
            await VerifyExistingAsync(destination, key, expected, cancellationToken).ConfigureAwait(false);
            return;
        }
        var existingHead = await destination.HeadAsync(key, cancellationToken).ConfigureAwait(false);
        if (existingHead is not null)
        {
            await VerifyExistingAsync(destination, key, expected, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Last-resort path for a backend with no conditional-write primitive (FTP has none).
        // HEAD-then-PUT is not create-if-absent: a concurrent publisher can write between the
        // two. The write is therefore followed by a read-back, so a lost race is detected as a
        // digest mismatch rather than silently accepted.
        await destination.PutAsync(key, content, length, cancellationToken).ConfigureAwait(false);
        await VerifyExistingAsync(destination, key, expected, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Confirms a stored object matches its expected digest, hashing it as a stream.
    ///
    /// Every call site previously copied the whole object into a MemoryStream and then again
    /// through ToArray, so verifying a large blob cost twice its size in memory for a check
    /// that needs none.
    /// </summary>
    private static async ValueTask VerifyExistingAsync(IWritableObjectStore destination, ObjectKey key, ContentHash expected, CancellationToken cancellationToken)
    {
        if (destination is IServerSideVerifier verifier)
        {
            if (!await verifier.VerifyAsync(key, expected, cancellationToken).ConfigureAwait(false))
                throw new CryptographicException($"Existing immutable object '{key}' failed verification.");
            return;
        }
        var existing = await destination.OpenAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Immutable object '{key}' disappeared during verification.");
        await using (existing.ConfigureAwait(false))
            if (await ContentHash.ComputeAsync(existing.Content, expected.Algorithm, cancellationToken).ConfigureAwait(false) != expected)
                throw new CryptographicException($"Existing immutable object '{key}' failed verification.");
    }
    private sealed record FileRow([property: JsonPropertyName("p")] string Path, [property: JsonPropertyName("h")] string? Hash, [property: JsonPropertyName("s")] long Size, [property: JsonPropertyName("m")] string? Md5, [property: JsonPropertyName("pol")] FileInstallPolicy? Policy, [property: JsonPropertyName("k")] FileEntryKind? Kind, [property: JsonPropertyName("mode")] string? Mode);
}
