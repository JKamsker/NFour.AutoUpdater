namespace FourSaas.AutoUpdater.Publishing;

public sealed record PublishedPackage(PackageManifest Manifest, byte[] ManifestBytes, ImmutableDictionary<VirtualPath, ContentHash> Blobs);

public sealed class PackageBuilder
{
    public async ValueTask<PublishedPackage> BuildAsync(SlicedPackage package, PackageVersion version, RepositoryLayout layout, IWritableObjectStore destination, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        timeProvider ??= TimeProvider.System;
        var files = new List<(PackageFileEntry Entry, byte[] Row)>();
        var blobs = ImmutableDictionary.CreateBuilder<VirtualPath, ContentHash>();
        foreach (var sliced in package.Files.OrderBy(x => x.Destination.Value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = sliced.Kind == FileEntryKind.Directory ? ContentHash.Compute([]) : default;
            var length = 0L;
            if (sliced.Kind == FileEntryKind.File)
            {
                await using var source = new FileStream(sliced.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                hash = await ContentHash.ComputeAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false);
                length = source.Length;
                source.Position = 0;
                blobs[sliced.Destination] = hash;
                await PutOnceAsync(destination, layout.Blob(hash), source, hash, length, cancellationToken).ConfigureAwait(false);
            }
            var entry = new PackageFileEntry { Path = sliced.Destination, Content = hash, Size = length, Policy = sliced.Policy, Kind = sliced.Kind, Mode = sliced.Mode };
            var row = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new FileRow(entry.Path.Value, sliced.Kind == FileEntryKind.File ? entry.Content.ToString() : null, entry.Size, entry.Policy == FileInstallPolicy.Replace ? null : entry.Policy, entry.Kind == FileEntryKind.File ? null : entry.Kind, entry.Mode), RepositoryJson.Options) + "\n");
            files.Add((entry, row));
        }

        var shardCount = NextPowerOfTwo(Math.Max(1, (files.Count + 4095) / 4096));
        var shardData = Enumerable.Range(0, shardCount).Select(_ => new MemoryStream()).ToArray();
        foreach (var file in files)
        {
            var index = FileTableSharding.GetShardIndex(file.Entry.Path, shardCount);
            shardData[index].Write(file.Row);
        }
        var shardRefs = ImmutableArray.CreateBuilder<FileTableShardRef>();
        for (var index = 0; index < shardCount; index++)
        {
            var data = shardData[index].ToArray();
            var digest = ContentHash.Compute(data);
            await using var shard = new MemoryStream(data, writable: false);
            await PutOnceAsync(destination, layout.Blob(digest), shard, digest, data.LongLength, cancellationToken).ConfigureAwait(false);
            shardRefs.Add(new FileTableShardRef { Index = index, Digest = digest, Count = files.Count(x => FileTableSharding.GetShardIndex(x.Entry.Path, shardCount) == index), Size = data.LongLength });
        }
        var table = new FileTableRef { Format = "jsonl/v1", ShardCount = shardCount, Shards = shardRefs.ToImmutable(), Digest = FileTableSharding.ComputeTableDigest(shardRefs) };
        var downloadSize = files.Where(x => x.Entry.Kind == FileEntryKind.File).GroupBy(x => x.Entry.Content).Sum(x => x.First().Entry.Size);
        var manifest = new PackageManifest { SchemaVersion = 1, Id = package.Id, Version = version, Sequence = version.Sequence, Kind = PackageKind.Content, CreatedAt = timeProvider.GetUtcNow(), Requires = package.Requires, PathPrefixes = files.Select(x => Prefix(x.Entry.Path.Value)).Distinct(StringComparer.Ordinal).ToImmutableArray(), FileTable = table, FileCount = files.Count, InstallSize = files.Sum(x => x.Entry.Size), DownloadSize = downloadSize };
        var manifestBytes = RepositoryJson.SerializeManifest(manifest);
        await using var manifestStream = new MemoryStream(manifestBytes, writable: false);
        await PutOnceAsync(destination, layout.Package(package.Id, version), manifestStream, ContentHash.Compute(manifestBytes), manifestBytes.LongLength, cancellationToken).ConfigureAwait(false);
        return new PublishedPackage(manifest, manifestBytes, blobs.ToImmutable());
    }

    private static int NextPowerOfTwo(int value) { var result = 1; while (result < value) result <<= 1; return result; }
    private static string Prefix(string path) { var slash = path.IndexOf('/'); return slash < 0 ? path : path[..(slash + 1)]; }
    private static async ValueTask PutOnceAsync(IWritableObjectStore destination, ObjectKey key, Stream content, ContentHash expected, long length, CancellationToken cancellationToken)
    {
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
    private sealed record FileRow([property: JsonPropertyName("p")] string Path, [property: JsonPropertyName("h")] string? Hash, [property: JsonPropertyName("s")] long Size, [property: JsonPropertyName("pol")] FileInstallPolicy? Policy, [property: JsonPropertyName("k")] FileEntryKind? Kind, [property: JsonPropertyName("mode")] string? Mode);
}
