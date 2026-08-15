using Amazon.S3;

namespace NFour.AutoUpdater.Storage.S3;

/// <summary>Enables conditional writes for provider profiles that guarantee create-if-absent semantics.</summary>
public sealed class S3ConditionalObjectStore : S3ObjectStore, IConditionalWriteStore, IContentAddressedWriteStore
{
    /// <summary>Initializes an S3 store that requires conditional-write support.</summary>
    public S3ConditionalObjectStore(string bucket, string prefix = "", IAmazonS3? client = null, Uri? serviceUrl = null, string? accessKey = null, string? secretKey = null, S3ProviderProfile providerProfile = S3ProviderProfile.Aws)
        : base(bucket, prefix, client, serviceUrl, accessKey, secretKey, providerProfile)
    {
        if (!SupportsConditionalWrites) throw new ArgumentException($"Provider profile '{providerProfile}' does not support conditional writes.", nameof(providerProfile));
    }

    /// <inheritdoc />
    public override StorageCapabilities Capabilities => base.Capabilities | StorageCapabilities.ConditionalWrite;
}
