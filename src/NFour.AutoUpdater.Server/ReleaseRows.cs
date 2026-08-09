namespace NFour.AutoUpdater.Server;

/// <summary>Stores an authored release draft.</summary>
public sealed class ReleaseDraftRow
{
    /// <summary>Gets or sets the repository identifier.</summary>
    public required string RepositoryId { get; set; }
    /// <summary>Gets or sets the draft identifier.</summary>
    public required string DraftId { get; set; }
    /// <summary>Gets or sets the product identifier.</summary>
    public required string ProductId { get; set; }
    /// <summary>Gets or sets the serialized draft payload.</summary>
    public required string PayloadJson { get; set; }
    /// <summary>Gets or sets when the draft was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Indexes one product release.</summary>
public sealed class ReleaseRow
{
    /// <summary>Gets or sets the repository identifier.</summary>
    public required string RepositoryId { get; set; }
    /// <summary>Gets or sets the product identifier.</summary>
    public required string ProductId { get; set; }
    /// <summary>Gets or sets the release identifier.</summary>
    public required string ReleaseId { get; set; }
    /// <summary>Gets or sets the monotonic release sequence.</summary>
    public long Sequence { get; set; }
    /// <summary>Gets or sets the release-envelope digest.</summary>
    public required string EnvelopeDigest { get; set; }
    /// <summary>Gets or sets the release-coverage digest.</summary>
    public required string CoverageDigest { get; set; }
    /// <summary>Gets or sets whether publication completed.</summary>
    public bool Published { get; set; }
}

/// <summary>Stores a channel pointer to a release.</summary>
public sealed class ChannelRow
{
    /// <summary>Gets or sets the repository identifier.</summary>
    public required string RepositoryId { get; set; }
    /// <summary>Gets or sets the product identifier.</summary>
    public required string ProductId { get; set; }
    /// <summary>Gets or sets the channel name.</summary>
    public required string Channel { get; set; }
    /// <summary>Gets or sets the monotonic channel sequence.</summary>
    public long ChannelSequence { get; set; }
    /// <summary>Gets or sets the referenced release identifier.</summary>
    public required string ReleaseId { get; set; }
    /// <summary>Gets or sets the signed pointer digest.</summary>
    public required string PointerDigest { get; set; }
}
