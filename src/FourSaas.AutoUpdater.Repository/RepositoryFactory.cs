namespace FourSaas.AutoUpdater.Repository;

public static class RepositoryFactory
{
    public static async ValueTask<(RepositoryDescriptor Descriptor, RepositoryLayout Layout)> LoadDescriptorAsync(IReadableObjectStore store, CancellationToken cancellationToken = default)
    {
        var result = await store.OpenAsync(new ObjectKey("repo.json"), cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException("Repository descriptor repo.json is missing.");
        await using (result.ConfigureAwait(false))
        {
            using var bytes = new MemoryStream(); await result.Content.CopyToAsync(bytes, cancellationToken).ConfigureAwait(false);
            var descriptor = RepositoryJson.Deserialize<RepositoryDescriptor>(bytes.ToArray());
            if (descriptor.SchemaVersion != 1) throw new FormatException($"Unsupported repository schemaVersion {descriptor.SchemaVersion}; minimumClientVersion={descriptor.MinimumClientVersion ?? "unknown"}.");
            if (!string.Equals(descriptor.ContentHashAlgorithm, "sha256", StringComparison.OrdinalIgnoreCase)) throw new FormatException("This client supports only the normative sha256 repository CAS.");
            return (descriptor, new RepositoryLayout(descriptor.Layout));
        }
    }
}
