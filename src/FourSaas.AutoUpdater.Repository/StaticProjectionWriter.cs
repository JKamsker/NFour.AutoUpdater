using FourSaas.AutoUpdater.Core;
using FourSaas.AutoUpdater.Storage;

namespace FourSaas.AutoUpdater.Repository;

/// <summary>
/// Writes the unsigned, regenerable projections of the repository.  These objects are never
/// consulted for an install decision; the signed lock and digest chain remain authoritative.
/// </summary>
public sealed class StaticProjectionWriter(IWritableObjectStore store, RepositoryLayout layout)
{
    private const int ProjectionPageSize = 1000;
    public ValueTask WriteRepositoryDescriptorAsync(RepositoryDescriptor descriptor, CancellationToken cancellationToken = default)
        => WriteDerivedAsync(new ObjectKey("repo.json"), RepositoryJson.Serialize(descriptor), cancellationToken);

    public ValueTask WriteProductAsync(ProductDescriptor product, CancellationToken cancellationToken = default)
    {
        if (!Identifier.IsValid(product.ProductId, 128)) throw new FormatException("Product id must be a valid lowercase ASCII segment.");
        if (product.SchemaVersion != 1) throw new FormatException($"Unsupported product schemaVersion {product.SchemaVersion}.");
        return WriteDerivedAsync(layout.Product(product.ProductId), RepositoryJson.Serialize(product), cancellationToken);
    }

    public ValueTask WritePackageIndexAsync(PackageId packageId, IEnumerable<PackageManifest> manifests, CancellationToken cancellationToken = default)
        => WritePackageIndexAsync(packageId, manifests, exactManifestBytes: null, cancellationToken);

    public async ValueTask WritePackageIndexAsync(PackageId packageId, IEnumerable<PackageManifest> manifests, IReadOnlyDictionary<PackageId, byte[]>? exactManifestBytes, CancellationToken cancellationToken = default)
    {
        var versions = manifests
            .Where(x => x.Id == packageId)
            .OrderByDescending(x => x.Sequence == 0 ? x.Version.Sequence : x.Sequence)
            .ThenByDescending(x => x.Version.Label, StringComparer.Ordinal)
            .Select(x => new PackageIndexVersion
            {
                Version = x.Version.Label,
                Sequence = x.Sequence == 0 ? x.Version.Sequence : x.Sequence,
                ManifestPath = layout.Package(x.Id, x.Version).Value,
                ManifestDigest = exactManifestBytes is not null && exactManifestBytes.TryGetValue(x.Id, out var exactBytes)
                    ? ContentHash.Compute(exactBytes)
                    : ContentHash.Compute(RepositoryJson.SerializeManifest(x)),
                FileCount = x.FileCount,
                InstallSize = x.InstallSize,
                DownloadSize = x.DownloadSize
            })
            .ToArray();
        var pages = versions.Length == 0 ? [Array.Empty<PackageIndexVersion>()] : versions.Chunk(ProjectionPageSize).Select(page => page.ToArray()).ToArray();
        for (var page = 0; page < pages.Length; page++)
        {
            var bytes = RepositoryJson.Serialize(new PackageIndexDocument { SchemaVersion = 1, PackageId = packageId.Value, Versions = pages[page] });
            await WriteDerivedAsync(layout.PackageIndex(packageId, page), bytes, cancellationToken).ConfigureAwait(false);
        }
        await DeleteStalePagesAsync(page => layout.PackageIndex(packageId, page), pages.Length, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteReleaseIndexAsync(string productId, IEnumerable<(ReleaseLock Lock, byte[] EnvelopeBytes)> releases, CancellationToken cancellationToken = default)
    {
        var rows = releases
            .Where(x => string.Equals(x.Lock.ProductId, productId, StringComparison.Ordinal) && x.Lock.State == ReleaseState.Published)
            .OrderByDescending(x => x.Lock.Sequence)
            .ThenByDescending(x => x.Lock.ReleaseId, StringComparer.Ordinal)
            .Select(x => new ReleaseIndexRow
            {
                ReleaseId = x.Lock.ReleaseId,
                Sequence = x.Lock.Sequence,
                LockPath = layout.Release(x.Lock.ProductId, x.Lock.ReleaseId).Value,
                LockDigest = ContentHash.Compute(x.EnvelopeBytes),
                CoverageDigest = x.Lock.CoverageDigest,
                CreatedAt = x.Lock.CreatedAt
            })
            .ToArray();
        var pages = rows.Length == 0 ? [Array.Empty<ReleaseIndexRow>()] : rows.Chunk(ProjectionPageSize).Select(page => page.ToArray()).ToArray();
        for (var page = 0; page < pages.Length; page++)
        {
            var bytes = RepositoryJson.Serialize(new ReleaseIndexDocument { SchemaVersion = 1, ProductId = productId, Releases = pages[page] });
            await WriteDerivedAsync(layout.ReleaseIndex(productId, page), bytes, cancellationToken).ConfigureAwait(false);
        }
        await DeleteStalePagesAsync(page => layout.ReleaseIndex(productId, page), pages.Length, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask WriteReleaseBundleAsync(string productId, string releaseId, ReadOnlyMemory<byte> lockEnvelopeBytes, IEnumerable<PackageManifest> manifests, CancellationToken cancellationToken = default)
        => WriteReleaseBundleAsync(productId, releaseId, lockEnvelopeBytes, manifests, exactManifestBytes: null, cancellationToken);

    public async ValueTask WriteReleaseBundleAsync(string productId, string releaseId, ReadOnlyMemory<byte> lockEnvelopeBytes, IEnumerable<PackageManifest> manifests, IReadOnlyDictionary<PackageId, byte[]>? exactManifestBytes, CancellationToken cancellationToken = default)
    {
        var envelope = SignedDocument.DeserializeEnvelope(lockEnvelopeBytes.Span);
        if (!string.Equals(envelope.Type, "release-lock", StringComparison.Ordinal)) throw new InvalidDataException("A release bundle requires a release-lock envelope.");
        var inline = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var manifest in manifests.OrderBy(x => x.Id.Value, StringComparer.Ordinal))
        {
            var bytes = exactManifestBytes is not null && exactManifestBytes.TryGetValue(manifest.Id, out var exactBytes)
                ? exactBytes
                : RepositoryJson.SerializeManifest(manifest);
            using var document = JsonDocument.Parse(bytes);
            inline.Add(manifest.Id.Value, document.RootElement.Clone());
        }
        var bundle = new BundleDocument { SchemaVersion = 1, LockEnvelope = Base64Url.Encode(lockEnvelopeBytes.Span), Inline = inline };
        await WriteDerivedAsync(layout.ReleaseBundle(productId, releaseId), RepositoryJson.Serialize(bundle), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteDerivedAsync(ObjectKey key, byte[] bytes, CancellationToken cancellationToken)
    {
        if (store is not IConditionalWriteStore conditional)
        {
            await using var body = new MemoryStream(bytes, writable: false);
            await store.PutAsync(key, body, bytes.LongLength, cancellationToken).ConfigureAwait(false);
            return;
        }

        for (var attempt = 0; attempt != 8; attempt++)
        {
            var head = await store.HeadAsync(key, cancellationToken).ConfigureAwait(false);
            if (head is null)
            {
                await using var first = new MemoryStream(bytes, writable: false);
                if (await conditional.PutIfAbsentAsync(key, first, bytes.LongLength, cancellationToken).ConfigureAwait(false)) return;
                continue;
            }
            if (head.Validator is null) throw new IOException($"Derived projection '{key}' has no conditional-write validator.");
            await using var replacement = new MemoryStream(bytes, writable: false);
            if (await conditional.CompareAndSwapAsync(key, head.Validator, replacement, bytes.LongLength, cancellationToken).ConfigureAwait(false)) return;
        }
        throw new IOException($"Could not update derived projection '{key}' after concurrent-write retries.");
    }

    private async ValueTask DeleteStalePagesAsync(Func<int, ObjectKey> keyFactory, int firstStalePage, CancellationToken cancellationToken)
    {
        if (store is not IWritableObjectStore writable) return;
        for (var page = firstStalePage; page < firstStalePage + 4096; page++)
        {
            var key = keyFactory(page);
            if (await store.HeadAsync(key, cancellationToken).ConfigureAwait(false) is null) break;
            await writable.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record PackageIndexDocument
    {
        public required int SchemaVersion { get; init; }
        public required string PackageId { get; init; }
        public required IReadOnlyList<PackageIndexVersion> Versions { get; init; }
    }

    private sealed record PackageIndexVersion
    {
        public required string Version { get; init; }
        public required long Sequence { get; init; }
        public required string ManifestPath { get; init; }
        public required ContentHash ManifestDigest { get; init; }
        public required int FileCount { get; init; }
        public required long InstallSize { get; init; }
        public required long DownloadSize { get; init; }
    }

    private sealed record ReleaseIndexDocument
    {
        public required int SchemaVersion { get; init; }
        public required string ProductId { get; init; }
        public required IReadOnlyList<ReleaseIndexRow> Releases { get; init; }
    }

    private sealed record ReleaseIndexRow
    {
        public required string ReleaseId { get; init; }
        public required long Sequence { get; init; }
        public required string LockPath { get; init; }
        public required ContentHash LockDigest { get; init; }
        public required ContentHash CoverageDigest { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
    }

    private sealed record BundleDocument
    {
        public required int SchemaVersion { get; init; }
        public required string LockEnvelope { get; init; }
        public required IReadOnlyDictionary<string, JsonElement> Inline { get; init; }
    }
}
