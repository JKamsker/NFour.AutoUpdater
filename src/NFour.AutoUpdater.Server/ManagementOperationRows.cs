namespace NFour.AutoUpdater.Server;

/// <summary>Stores a bounded publish-session lease.</summary>
public sealed class PublishSessionRow
{
    /// <summary>Gets or sets the session identifier.</summary>
    public required string SessionId { get; set; }
    /// <summary>Gets or sets the repository identifier.</summary>
    public required string RepositoryId { get; set; }
    /// <summary>Gets or sets when the session expires.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
    /// <summary>Gets or sets the maximum permitted object count.</summary>
    public int MaxObjects { get; set; }
    /// <summary>Gets or sets the maximum permitted total byte count.</summary>
    public long MaxTotalBytes { get; set; }
    /// <summary>Gets or sets the granted object count.</summary>
    public int ObjectCount { get; set; }
    /// <summary>Gets or sets the granted total byte count.</summary>
    public long TotalBytes { get; set; }
    /// <summary>Gets or sets whether the session has been sealed.</summary>
    public bool Sealed { get; set; }
}

/// <summary>Stores an auditable management action and outcome.</summary>
public sealed class AuditEventRow
{
    /// <summary>Gets or sets the database identity.</summary>
    public long Id { get; set; }
    /// <summary>Gets or sets when the event occurred.</summary>
    public DateTimeOffset At { get; set; }
    /// <summary>Gets or sets the authenticated actor.</summary>
    public required string Actor { get; set; }
    /// <summary>Gets or sets the action name.</summary>
    public required string Action { get; set; }
    /// <summary>Gets or sets the affected resource.</summary>
    public required string Resource { get; set; }
    /// <summary>Gets or sets the action outcome.</summary>
    public required string Outcome { get; set; }
}

/// <summary>Stores a trusted public signing key.</summary>
public sealed class TrustedKeyRow
{
    /// <summary>Gets or sets the repository identifier.</summary>
    public required string RepositoryId { get; set; }
    /// <summary>Gets or sets the signing-key identifier.</summary>
    public required string KeyId { get; set; }
    /// <summary>Gets or sets the encoded public key.</summary>
    public required string PublicKey { get; set; }
    /// <summary>Gets or sets when the key was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Stores the current signed product-revocation document.</summary>
public sealed class RevocationRow
{
    /// <summary>Gets or sets the repository identifier.</summary>
    public required string RepositoryId { get; set; }
    /// <summary>Gets or sets the product identifier.</summary>
    public required string ProductId { get; set; }
    /// <summary>Gets or sets the serialized signed revocation envelope.</summary>
    public required string EnvelopeJson { get; set; }
    /// <summary>Gets or sets when the revocation was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Stores the lifecycle and multipart state of a publish upload grant.</summary>
public sealed class PublishGrantRow
{
    /// <summary>Gets or sets the grant identifier.</summary>
    public required string GrantId { get; set; }
    /// <summary>Gets or sets the owning session identifier.</summary>
    public required string SessionId { get; set; }
    /// <summary>Gets or sets the repository identifier.</summary>
    public required string RepositoryId { get; set; }
    /// <summary>Gets or sets the staging object key.</summary>
    public required string StagingKey { get; set; }
    /// <summary>Gets or sets the expected content digest.</summary>
    public required string Digest { get; set; }
    /// <summary>Gets or sets the expected byte length.</summary>
    public long Length { get; set; }
    /// <summary>Gets or sets when the grant expires.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
    /// <summary>Gets or sets the legacy consumed-state flag.</summary>
    public bool Used { get; set; }
    /// <summary>Gets or sets the grant lifecycle status.</summary>
    public string Status { get; set; } = GrantLifecycleStatus.Issued;
    /// <summary>Gets or sets when promotion claimed the grant.</summary>
    public DateTimeOffset? ClaimedAt { get; set; }
    /// <summary>Gets or sets when promotion consumed the grant.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }
    /// <summary>Gets or sets when the grant was invalidated.</summary>
    public DateTimeOffset? InvalidatedAt { get; set; }
    /// <summary>Gets or sets the provider multipart-upload identifier.</summary>
    public string? MultipartUploadId { get; set; }
    /// <summary>Gets or sets the multipart part size.</summary>
    public long? MultipartPartSize { get; set; }
    /// <summary>Gets or sets whether the multipart upload completed.</summary>
    public bool MultipartCompleted { get; set; }
    /// <summary>Gets or sets whether multipart completion is in progress.</summary>
    public bool MultipartCompleting { get; set; }
    /// <summary>Gets or sets serialized completed-part metadata.</summary>
    public string? MultipartPartsJson { get; set; }
}
