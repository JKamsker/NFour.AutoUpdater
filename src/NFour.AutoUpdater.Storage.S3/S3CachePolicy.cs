namespace NFour.AutoUpdater.Storage.S3;

internal static class S3CachePolicy
{
    private const string RepositoryDescriptorKey = "repo.json";
    private const string KeyManifestKey = "keys.json";
    private const string ChannelPathSegment = "/channels/";
    private const string RevocationSuffix = "/revocations.json";
    private const string FirstIndexPageSuffix = "/index.json";
    private const string AdditionalIndexPageSegment = "/index.";
    private const string ProductDescriptorSuffix = "/product.json";
    private const string RepositoryDescriptorPolicy = "max-age=300";
    private const string MutableProjectionPolicy = "max-age=30, must-revalidate";
    private const string ImmutableObjectPolicy = "public, max-age=31536000, immutable";

    public static string For(ObjectKey key)
    {
        if (key.Value == RepositoryDescriptorKey) return RepositoryDescriptorPolicy;
        if (key.Value.Contains(ChannelPathSegment, StringComparison.Ordinal)) return MutableProjectionPolicy;
        if (key.Value == KeyManifestKey
            || key.Value.EndsWith(RevocationSuffix, StringComparison.Ordinal)
            || key.Value.EndsWith(FirstIndexPageSuffix, StringComparison.Ordinal)
            || key.Value.Contains(AdditionalIndexPageSegment, StringComparison.Ordinal)
            || key.Value.EndsWith(ProductDescriptorSuffix, StringComparison.Ordinal))
            return MutableProjectionPolicy;
        return ImmutableObjectPolicy;
    }
}
