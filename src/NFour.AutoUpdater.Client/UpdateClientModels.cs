namespace NFour.AutoUpdater.Client;

/// <summary>Describes one trusted, non-interactive install or update operation.</summary>
public sealed record UpdateClientRequest
{
    /// <summary>Gets the absolute URI of the repository descriptor.</summary>
    public required Uri RepositoryUri { get; init; }
    /// <summary>Gets the product whose channel should be resolved.</summary>
    public required string ProductId { get; init; }
    /// <summary>Gets the signed channel to follow.</summary>
    public required string Channel { get; init; }
    /// <summary>Gets the installation directory to update.</summary>
    public required string InstallRoot { get; init; }
    /// <summary>Gets the requested package-variant selection.</summary>
    public required VariantSelection Selection { get; init; }
    /// <summary>Gets the out-of-band Ed25519 trust roots, indexed by key identifier.</summary>
    public required IReadOnlyDictionary<string, byte[]> TrustedKeys { get; init; }
    /// <summary>Gets an optional key identifier that must authenticate key rotation.</summary>
    public string? PinnedRootKeyId { get; init; }
    /// <summary>Gets an optional shared content-cache directory.</summary>
    public string? CacheRoot { get; init; }
    /// <summary>Gets the client version used for minimum-version policy checks.</summary>
    public Version? ClientVersion { get; init; }
    /// <summary>Gets an optional time provider for policy evaluation.</summary>
    public TimeProvider? TimeProvider { get; init; }
    /// <summary>Gets the maximum accepted age of signed channel metadata.</summary>
    public TimeSpan ChannelStalenessBound { get; init; } = TimeSpan.FromDays(7);
    /// <summary>Gets an optional maximum age for the revocation document.</summary>
    public TimeSpan? RevocationStalenessBound { get; init; }
    /// <summary>Gets the portion of the resolved package graph to materialize.</summary>
    public UpdatePackageScope PackageScope { get; init; } = UpdatePackageScope.Composed;
}

/// <summary>Specifies which part of a resolved release an update operation materializes.</summary>
public enum UpdatePackageScope
{
    /// <summary>Materializes the complete base and variant selection.</summary>
    Composed,
    /// <summary>Materializes only packages that are always required.</summary>
    Base,
    /// <summary>Materializes only packages selected through variant conditions.</summary>
    Variants
}

/// <summary>Reports the signed release and concrete file plan applied by the updater.</summary>
/// <param name="ReleaseId">The accepted release identifier.</param>
/// <param name="ReleaseSequence">The accepted anti-rollback sequence.</param>
/// <param name="FileSetId">The digest of the materialized file set.</param>
/// <param name="Changed">Whether the plan contained an operation other than keep.</param>
/// <param name="Plan">The applied file-operation plan.</param>
/// <param name="Selection">The normalized selection persisted for this layer.</param>
public sealed record UpdateClientResult(
    string ReleaseId,
    long ReleaseSequence,
    ContentHash FileSetId,
    bool Changed,
    InstallPlan Plan,
    VariantSelection Selection);
