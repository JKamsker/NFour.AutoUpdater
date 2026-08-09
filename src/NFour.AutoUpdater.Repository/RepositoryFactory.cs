namespace NFour.AutoUpdater.Repository;

public static class RepositoryFactory
{
    public static async ValueTask<(RepositoryDescriptor Descriptor, RepositoryLayout Layout)> LoadDescriptorAsync(IReadableObjectStore store, CancellationToken cancellationToken = default)
    {
        if (!StorageCapabilityNegotiation.IsConsistent(store)) throw new FormatException("The storage backend advertises capabilities inconsistent with its implemented interfaces.");
        var result = await store.OpenAsync(new ObjectKey("repo.json"), cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException("Repository descriptor repo.json is missing.");
        await using (result.ConfigureAwait(false))
        {
            using var bytes = new MemoryStream(); await result.Content.CopyToAsync(bytes, cancellationToken).ConfigureAwait(false);
            var descriptor = RepositoryJson.Deserialize<RepositoryDescriptor>(bytes.ToArray());
            if (descriptor.SchemaVersion != 1) throw new FormatException($"Unsupported repository schemaVersion {descriptor.SchemaVersion}; minimumClientVersion={descriptor.MinimumClientVersion ?? "unknown"}.");
            if (!string.Equals(descriptor.ContentHashAlgorithm, "sha256", StringComparison.OrdinalIgnoreCase)) throw new FormatException("This client supports only the normative sha256 repository CAS.");
            var effectiveCapabilities = StorageCapabilityNegotiation.Intersect(descriptor, store);
            // HTTP advertises range support at the transport type level, but the
            // origin/CDN is authoritative for a concrete repository. A HEAD probe
            // prevents resume logic from treating an origin that only serves 200
            // responses as range-capable.
            var descriptorHead = await store.HeadAsync(new ObjectKey("repo.json"), cancellationToken).ConfigureAwait(false);
            if (descriptorHead is { AcceptRanges: false }) effectiveCapabilities &= ~StorageCapabilities.Range;
            var effectiveDescriptor = descriptor with { Capabilities = effectiveCapabilities };
            return (effectiveDescriptor, new RepositoryLayout(effectiveDescriptor.Layout));
        }
    }
}
