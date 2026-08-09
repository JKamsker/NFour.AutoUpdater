namespace NFour.AutoUpdater.Storage.S3;

/// <summary>Identifies provider-specific S3 compatibility guarantees.</summary>
public enum S3ProviderProfile
{
    /// <summary>Amazon Web Services S3.</summary>
    Aws,
    /// <summary>MinIO.</summary>
    Minio,
    /// <summary>Cloudflare R2.</summary>
    R2,
    /// <summary>Backblaze B2 S3 compatibility.</summary>
    B2,
    /// <summary>An S3-compatible provider with no additional guarantees.</summary>
    Generic
}
