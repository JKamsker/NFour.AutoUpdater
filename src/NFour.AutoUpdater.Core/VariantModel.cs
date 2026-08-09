namespace NFour.AutoUpdater.Core;

/// <summary>Describes a sequence constraint on a required package.</summary>
public sealed record PackageDependency
{
    /// <summary>Gets the required package identifier.</summary>
    public required PackageId Id { get; init; }
    /// <summary>Gets the inclusive minimum sequence, if constrained.</summary>
    public long? MinSequence { get; init; }
    /// <summary>Gets the inclusive maximum sequence, if constrained.</summary>
    public long? MaxSequence { get; init; }
}
/// <summary>Describes an entry in a package file table.</summary>
public sealed record PackageFileEntry
{
    /// <summary>Gets the portable installation path.</summary>
    public required VirtualPath Path { get; init; }
    /// <summary>Gets the content digest.</summary>
    public required ContentHash Content { get; init; }
    /// <summary>Gets the content length in bytes.</summary>
    public required long Size { get; init; }
    /// <summary>Gets the optional MD5 digest required by a storage backend.</summary>
    public ContentHash? Md5 { get; init; }
    /// <summary>Gets the installation policy.</summary>
    public FileInstallPolicy Policy { get; init; } = FileInstallPolicy.Replace;
    /// <summary>Gets the entry kind.</summary>
    public FileEntryKind Kind { get; init; } = FileEntryKind.File;
    /// <summary>Gets the optional portable POSIX mode text.</summary>
    public string? Mode { get; init; }
}

/// <summary>Identifies and describes one immutable file-table shard.</summary>
public sealed record FileTableShardRef
{
    /// <summary>Gets the zero-based shard index.</summary>
    public required int Index { get; init; }
    /// <summary>Gets the shard content digest.</summary>
    public required ContentHash Digest { get; init; }
    /// <summary>Gets the number of file entries in the shard.</summary>
    public required int Count { get; init; }
    /// <summary>Gets the encoded shard length in bytes.</summary>
    public required long Size { get; init; }
}

/// <summary>Describes the sharded file table referenced by a package manifest.</summary>
public sealed record FileTableRef
{
    /// <summary>Gets the file-table encoding identifier.</summary>
    public required string Format { get; init; }
    /// <summary>Gets the declared number of shards.</summary>
    public required int ShardCount { get; init; }
    /// <summary>Gets the ordered shard descriptors.</summary>
    public required ImmutableArray<FileTableShardRef> Shards { get; init; }
    /// <summary>Gets the digest covering the complete logical file table.</summary>
    public required ContentHash Digest { get; init; }
}

/// <summary>Describes an immutable, versioned package and its file table.</summary>
public sealed record PackageManifest
{
    /// <summary>Gets the document schema version.</summary>
    public required int SchemaVersion { get; init; }
    /// <summary>Gets the package identifier.</summary>
    public required PackageId Id { get; init; }
    /// <summary>Gets the package version.</summary>
    public required PackageVersion Version { get; init; }
    /// <summary>Gets the monotonic package sequence.</summary>
    public long Sequence { get; init; }
    /// <summary>Gets the package role.</summary>
    public required PackageKind Kind { get; init; }
    /// <summary>Gets the manifest creation time.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
    /// <summary>Gets packages that cannot coexist with this package.</summary>
    public ImmutableArray<PackageId> Conflicts { get; init; } = [];
    /// <summary>Gets package dependencies.</summary>
    public ImmutableArray<PackageDependency> Requires { get; init; } = [];
    /// <summary>Gets the virtual path prefixes owned by the package.</summary>
    public required ImmutableArray<string> PathPrefixes { get; init; }
    /// <summary>Gets the package file-table descriptor.</summary>
    public required FileTableRef FileTable { get; init; }
    /// <summary>Gets the declared file count.</summary>
    public required int FileCount { get; init; }
    /// <summary>Gets the installed size in bytes.</summary>
    public required long InstallSize { get; init; }
    /// <summary>Gets the download size in bytes.</summary>
    public required long DownloadSize { get; init; }
    /// <summary>Gets optional extension metadata.</summary>
    public ImmutableDictionary<string, JsonElement>? Metadata { get; init; }
}

/// <summary>Describes when and at what precedence a package is selected.</summary>
public sealed record PackageRequirement
{
    /// <summary>Gets the required package identifier.</summary>
    public required PackageId Package { get; init; }
    /// <summary>Gets the variant predicate controlling selection.</summary>
    public AxisPredicate When { get; init; } = AxisPredicate.Always;
    /// <summary>Gets the axis whose rank supplies package precedence.</summary>
    public string? RankAs { get; init; }
    /// <summary>Gets an explicit precedence layer override.</summary>
    public int? LayerOverride { get; init; }
    /// <summary>Gets packages this package is permitted to shadow.</summary>
    public ImmutableArray<PackageId> Overrides { get; init; } = [];
    /// <summary>Gets whether a missing package pin is permitted.</summary>
    public bool Optional { get; init; }
}

/// <summary>Pins a package requirement to an immutable manifest.</summary>
public sealed record LockedPackage
{
    /// <summary>Gets the package identifier.</summary>
    public required PackageId Id { get; init; }
    /// <summary>Gets the package version.</summary>
    public required PackageVersion Version { get; init; }
    /// <summary>Gets the monotonic package sequence.</summary>
    public long Sequence { get; init; }
    /// <summary>Gets the repository-relative manifest path.</summary>
    public required string ManifestPath { get; init; }
    /// <summary>Gets the digest of the exact manifest bytes.</summary>
    public required ContentHash ManifestDigest { get; init; }
    /// <summary>Gets the declared file count.</summary>
    public required int FileCount { get; init; }
    /// <summary>Gets the installed size in bytes.</summary>
    public required long InstallSize { get; init; }
    /// <summary>Gets the download size in bytes.</summary>
    public required long DownloadSize { get; init; }
    /// <summary>Gets package dependencies copied into the lock.</summary>
    public ImmutableArray<PackageDependency> Requires { get; init; } = [];
    /// <summary>Gets packages this package is permitted to shadow.</summary>
    public ImmutableArray<PackageId> Overrides { get; init; } = [];
    /// <summary>Gets packages that cannot coexist with this package.</summary>
    public ImmutableArray<PackageId> Conflicts { get; init; } = [];
}

/// <summary>Contains a detached signature over canonical manifest bytes.</summary>
public sealed record ManifestSignature
{
    /// <summary>Gets the signing-key identifier.</summary>
    public required string KeyId { get; init; }
    /// <summary>Gets the signature algorithm identifier.</summary>
    public required string Algorithm { get; init; }
    /// <summary>Gets the encoded signature value.</summary>
    public required string Value { get; init; }
}

/// <summary>Pins the complete immutable package graph for a release.</summary>
public sealed record ReleaseLock
{
    /// <summary>Gets the document schema version.</summary>
    public required int SchemaVersion { get; init; }
    /// <summary>Gets the product identifier.</summary>
    public required string ProductId { get; init; }
    /// <summary>Gets the release identifier.</summary>
    public required string ReleaseId { get; init; }
    /// <summary>Gets the monotonic release sequence.</summary>
    public required long Sequence { get; init; }
    /// <summary>Gets the release lifecycle state.</summary>
    public required ReleaseState State { get; init; }
    /// <summary>Gets the release creation time.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
    /// <summary>Gets the declared variant axes.</summary>
    public required ImmutableArray<AxisDefinition> Axes { get; init; }
    /// <summary>Gets the conditional package requirements.</summary>
    public required ImmutableArray<PackageRequirement> Requirements { get; init; }
    /// <summary>Gets the immutable package pins.</summary>
    public required ImmutableArray<LockedPackage> Packages { get; init; }
    /// <summary>Gets the lowest installed release sequence from which this release may be reached.</summary>
    public long? MinimumInstalledRelease { get; init; }
    /// <summary>Gets the digest of the release coverage proof.</summary>
    public required ContentHash CoverageDigest { get; init; }
    /// <summary>Gets the optional legacy embedded signature.</summary>
    public ManifestSignature? Signature { get; init; }
}

/// <summary>Combines a release lock with optionally inlined package manifests.</summary>
public sealed record ReleaseBundle
{
    /// <summary>Gets the parsed release lock.</summary>
    public required ReleaseLock Lock { get; init; }
    /// <summary>Gets manifests inlined by package identifier.</summary>
    public required ImmutableDictionary<PackageId, PackageManifest> Inline { get; init; }
    /// <summary>Gets the encoded signed lock envelope, when present.</summary>
    public string? LockEnvelope { get; init; }
}

/// <summary>Represents a package selected for a concrete variant.</summary>
/// <param name="Requirement">The requirement that selected the package.</param>
/// <param name="Pin">The immutable package pin.</param>
/// <param name="Layer">The composition precedence layer.</param>
/// <param name="Discriminator">The precedence within the layer.</param>
public sealed record ResolvedPackage(PackageRequirement Requirement, LockedPackage Pin, int Layer, int Discriminator)
{
    /// <summary>Gets the selected package identifier.</summary>
    public PackageId Id => Pin.Id;
}

/// <summary>Contains the package resolution result for one variant selection.</summary>
public sealed record ResolutionResult
{
    /// <summary>Gets the normalized selection that was resolved.</summary>
    public required VariantSelection Selection { get; init; }
    /// <summary>Gets selected packages in composition order.</summary>
    public required ImmutableArray<ResolvedPackage> Packages { get; init; }
    /// <summary>Gets resolution diagnostics.</summary>
    public required ImmutableArray<Diagnostic> Diagnostics { get; init; }
    /// <summary>Gets whether resolution completed without error diagnostics.</summary>
    public bool IsValid => !Diagnostics.Any(static x => x.IsError);
}

/// <summary>Represents a package file after variant resolution and precedence composition.</summary>
/// <param name="Path">The portable installation path.</param>
/// <param name="Content">The content digest.</param>
/// <param name="Size">The content length in bytes.</param>
/// <param name="Owner">The package that owns the selected file.</param>
/// <param name="Policy">The installation policy.</param>
/// <param name="Kind">The file-table entry kind.</param>
/// <param name="Mode">The optional portable POSIX mode text.</param>
/// <param name="Layer">The package precedence layer.</param>
/// <param name="Discriminator">The precedence within the layer.</param>
public sealed record ComposedFile(
    VirtualPath Path,
    ContentHash Content,
    long Size,
    PackageId Owner,
    FileInstallPolicy Policy,
    FileEntryKind Kind = FileEntryKind.File,
    string? Mode = null,
    int Layer = 0,
    int Discriminator = -1);

/// <summary>Contains the deterministic file set composed for a resolved variant.</summary>
public sealed record ComposedFileSet
{
    /// <summary>Gets selected files keyed by portable path.</summary>
    public required ImmutableSortedDictionary<VirtualPath, ComposedFile> Files { get; init; }
    /// <summary>Gets lower-precedence files shadowed by selected files.</summary>
    public required ImmutableArray<ComposedFile> Shadowed { get; init; }
    /// <summary>Gets the deterministic identity of the selected file set.</summary>
    public required ContentHash FileSetId { get; init; }
    /// <summary>Gets composition diagnostics.</summary>
    public ImmutableArray<Diagnostic> Diagnostics { get; init; } = [];
    /// <summary>Gets whether composition completed without error diagnostics.</summary>
    public bool IsValid => !Diagnostics.Any(static x => x.IsError);
}
