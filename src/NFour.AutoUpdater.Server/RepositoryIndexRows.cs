namespace NFour.AutoUpdater.Server;

/// <summary>Stores a product registered in a repository.</summary>
public sealed class ProductRow
{
    /// <summary>Gets or sets the repository identifier.</summary>
    public required string RepositoryId { get; set; }
    /// <summary>Gets or sets the product identifier.</summary>
    public required string ProductId { get; set; }
    /// <summary>Gets or sets when the product was registered.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Indexes a package-version manifest and its publication state.</summary>
public sealed class PackageVersionRow
{
    /// <summary>Gets or sets the repository identifier.</summary>
    public required string RepositoryId { get; set; }
    /// <summary>Gets or sets the package identifier.</summary>
    public required string PackageId { get; set; }
    /// <summary>Gets or sets the package version label.</summary>
    public required string Version { get; set; }
    /// <summary>Gets or sets the repository-wide package sequence.</summary>
    public long Sequence { get; set; }
    /// <summary>Gets or sets the signed manifest digest.</summary>
    public required string ManifestDigest { get; set; }
    /// <summary>Gets or sets serialized variant conditions.</summary>
    public string? WhenJson { get; set; }
    /// <summary>Gets or sets the indexed file-table JSON.</summary>
    public string? FileTableJson { get; set; }
    /// <summary>Gets or sets whether the package version is published.</summary>
    public bool Published { get; set; }
}

/// <summary>Stores metadata for one content-addressed blob.</summary>
public sealed class BlobRefRow
{
    /// <summary>Gets or sets the digest algorithm.</summary>
    public required string Algorithm { get; set; }
    /// <summary>Gets or sets the digest bytes encoded as text.</summary>
    public required string Hash { get; set; }
    /// <summary>Gets or sets the blob length in bytes.</summary>
    public long Length { get; set; }
    /// <summary>Gets or sets when the blob was first observed.</summary>
    public DateTimeOffset FirstSeenAt { get; set; }
}

/// <summary>Records a blob's location and verification time in a storage backend.</summary>
public sealed class BlobPlacementRow
{
    /// <summary>Gets or sets the repository identifier.</summary>
    public required string RepositoryId { get; set; }
    /// <summary>Gets or sets the digest algorithm.</summary>
    public required string Algorithm { get; set; }
    /// <summary>Gets or sets the digest bytes encoded as text.</summary>
    public required string Hash { get; set; }
    /// <summary>Gets or sets the storage backend identifier.</summary>
    public required string BackendId { get; set; }
    /// <summary>Gets or sets the backend object key.</summary>
    public required string ObjectKey { get; set; }
    /// <summary>Gets or sets when the placement was last verified.</summary>
    public DateTimeOffset? VerifiedAt { get; set; }
}
