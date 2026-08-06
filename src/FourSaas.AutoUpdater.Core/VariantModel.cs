namespace FourSaas.AutoUpdater.Core;

public enum AxisCardinality { One, Many }
public enum PackageKind { Content, Meta }
public enum PackageState { Draft, Published, Yanked }
public enum ReleaseState { Draft, Published, Yanked }
public enum FileInstallPolicy { Replace, Preserve, Executable }
public enum FileEntryKind { File, Directory }

public sealed record AxisValue
{
    public required string Id { get; init; }
    public string? Display { get; init; }
}

public sealed record AxisDefinition
{
    public required string Name { get; init; }
    public required int Rank { get; init; }
    public required ImmutableArray<AxisValue> Values { get; init; }
    public string? Default { get; init; }
    public required AxisCardinality Cardinality { get; init; }
    public required bool Required { get; init; }
    public string? DisplayName { get; init; }
    public ImmutableDictionary<string, string> Retired { get; init; } = ImmutableDictionary<string, string>.Empty;

    public int IndexOf(string value)
    {
        for (var i = 0; i < Values.Length; i++) if (StringComparer.Ordinal.Equals(Values[i].Id, value)) return i;
        return -1;
    }

    public bool TryResolveRetired(string value, out string resolved, out bool retired, out string? error)
    {
        resolved = value;
        retired = false;
        error = null;
        for (var i = 0; i < 8 && Retired.TryGetValue(resolved, out var replacement); i++)
        {
            retired = true;
            resolved = replacement;
        }
        if (Retired.ContainsKey(resolved))
        {
            error = "Retirement mapping exceeds the eight-hop limit or contains a cycle.";
            return false;
        }
        if (IndexOf(resolved) < 0)
        {
            error = $"Retirement replacement '{resolved}' is not a live axis value.";
            return false;
        }
        return true;
    }

    public AxisDefinition NormalizeRetiredMappings()
    {
        var normalized = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var oldValue in Retired.Keys.OrderBy(static value => value, StringComparer.Ordinal))
        {
            if (!TryResolveRetired(oldValue, out var replacement, out _, out var error))
                throw new FormatException($"Axis '{Name}' has an invalid retirement mapping for '{oldValue}': {error}");
            normalized[oldValue] = replacement;
        }
        return this with { Retired = normalized.ToImmutable() };
    }
}

public sealed record VariantSelection
{
    public required ImmutableSortedDictionary<string, ImmutableSortedSet<string>> Axes { get; init; }
    public ImmutableSortedSet<string> this[string axis] => Axes.TryGetValue(axis, out var values) ? values : ImmutableSortedSet<string>.Empty;
    public bool Has(string axis, string value) => this[axis].Contains(value);
    public string ToCanonicalString() => string.Join(';', Axes
        .Where(x => !x.Value.IsEmpty)
        .OrderBy(x => x.Key, StringComparer.Ordinal)
        .Select(x => $"{x.Key}={string.Join(',', x.Value.OrderBy(static v => v, StringComparer.Ordinal))}"));
    public ContentHash SelectionId => ContentHash.Compute(Encoding.UTF8.GetBytes(ToCanonicalString()));
}

public sealed record AxisPredicate
{
    public required ImmutableSortedDictionary<string, ImmutableSortedSet<string>> Constraints { get; init; }
    public static AxisPredicate Always { get; } = new() { Constraints = ImmutableSortedDictionary<string, ImmutableSortedSet<string>>.Empty };
    public bool IsAlways => Constraints.IsEmpty;
    public bool Matches(VariantSelection selection) => Constraints.All(x => selection[x.Key].Overlaps(x.Value));

    public bool CanCoexistWith(AxisPredicate other, IReadOnlyDictionary<string, AxisDefinition> axes)
    {
        foreach (var (axisName, left) in Constraints)
        {
            if (!other.Constraints.TryGetValue(axisName, out var right)) continue;
            if (!axes.TryGetValue(axisName, out var definition)) return false;
            if (definition.Cardinality == AxisCardinality.One && !left.Overlaps(right)) return false;
        }
        return true;
    }
}

public sealed record PackageDependency
{
    public required PackageId Id { get; init; }
    public long? MinSequence { get; init; }
    public long? MaxSequence { get; init; }
}

public sealed record PackageFileEntry
{
    public required VirtualPath Path { get; init; }
    public required ContentHash Content { get; init; }
    public required long Size { get; init; }
    public ContentHash? Md5 { get; init; }
    public FileInstallPolicy Policy { get; init; } = FileInstallPolicy.Replace;
    public FileEntryKind Kind { get; init; } = FileEntryKind.File;
    public string? Mode { get; init; }
}

public sealed record FileTableShardRef
{
    public required int Index { get; init; }
    public required ContentHash Digest { get; init; }
    public required int Count { get; init; }
    public required long Size { get; init; }
}

public sealed record FileTableRef
{
    public required string Format { get; init; }
    public required int ShardCount { get; init; }
    public required ImmutableArray<FileTableShardRef> Shards { get; init; }
    public required ContentHash Digest { get; init; }
}

public sealed record PackageManifest
{
    public required int SchemaVersion { get; init; }
    public required PackageId Id { get; init; }
    public required PackageVersion Version { get; init; }
    public long Sequence { get; init; }
    public required PackageKind Kind { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public ImmutableArray<PackageId> Conflicts { get; init; } = [];
    public ImmutableArray<PackageDependency> Requires { get; init; } = [];
    public required ImmutableArray<string> PathPrefixes { get; init; }
    public required FileTableRef FileTable { get; init; }
    public required int FileCount { get; init; }
    public required long InstallSize { get; init; }
    public required long DownloadSize { get; init; }
    public ImmutableDictionary<string, JsonElement>? Metadata { get; init; }
}

public sealed record PackageRequirement
{
    public required PackageId Package { get; init; }
    public AxisPredicate When { get; init; } = AxisPredicate.Always;
    public string? RankAs { get; init; }
    public int? LayerOverride { get; init; }
    public ImmutableArray<PackageId> Overrides { get; init; } = [];
    public bool Optional { get; init; }
}

public sealed record LockedPackage
{
    public required PackageId Id { get; init; }
    public required PackageVersion Version { get; init; }
    public long Sequence { get; init; }
    public required string ManifestPath { get; init; }
    public required ContentHash ManifestDigest { get; init; }
    public required int FileCount { get; init; }
    public required long InstallSize { get; init; }
    public required long DownloadSize { get; init; }
    public ImmutableArray<PackageDependency> Requires { get; init; } = [];
    public ImmutableArray<PackageId> Overrides { get; init; } = [];
    public ImmutableArray<PackageId> Conflicts { get; init; } = [];
}

public sealed record ManifestSignature
{
    public required string KeyId { get; init; }
    public required string Algorithm { get; init; }
    public required string Value { get; init; }
}

public sealed record ReleaseLock
{
    public required int SchemaVersion { get; init; }
    public required string ProductId { get; init; }
    public required string ReleaseId { get; init; }
    public required long Sequence { get; init; }
    public required ReleaseState State { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required ImmutableArray<AxisDefinition> Axes { get; init; }
    public required ImmutableArray<PackageRequirement> Requirements { get; init; }
    public required ImmutableArray<LockedPackage> Packages { get; init; }
    public long? MinimumInstalledRelease { get; init; }
    public required ContentHash CoverageDigest { get; init; }
    public ManifestSignature? Signature { get; init; }
}

public sealed record ReleaseBundle
{
    public required ReleaseLock Lock { get; init; }
    public required ImmutableDictionary<PackageId, PackageManifest> Inline { get; init; }
    public string? LockEnvelope { get; init; }
}

public sealed record ResolvedPackage(PackageRequirement Requirement, LockedPackage Pin, int Layer, int Discriminator)
{
    public PackageId Id => Pin.Id;
}

public sealed record ResolutionResult
{
    public required VariantSelection Selection { get; init; }
    public required ImmutableArray<ResolvedPackage> Packages { get; init; }
    public required ImmutableArray<Diagnostic> Diagnostics { get; init; }
    public bool IsValid => !Diagnostics.Any(static x => x.IsError);
}

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

public sealed record ComposedFileSet
{
    public required ImmutableSortedDictionary<VirtualPath, ComposedFile> Files { get; init; }
    public required ImmutableArray<ComposedFile> Shadowed { get; init; }
    public required ContentHash FileSetId { get; init; }
    public ImmutableArray<Diagnostic> Diagnostics { get; init; } = [];
    public bool IsValid => !Diagnostics.Any(static x => x.IsError);
}

public interface IVariantResolver
{
    ResolutionResult Resolve(ReleaseLock release, VariantSelection requested);
}

public interface IPackageRepository
{
    ValueTask<PackageManifest?> GetManifestAsync(PackageId id, PackageVersion version, CancellationToken cancellationToken = default);
    IAsyncEnumerable<PackageFileEntry> ReadFileTableAsync(PackageManifest manifest, CancellationToken cancellationToken = default);
}

public interface IManifestDigestRepository
{
    ValueTask<PackageManifest?> GetManifestAsync(PackageId id, PackageVersion version, ContentHash expectedDigest, CancellationToken cancellationToken = default);
}

/// <summary>Repository implementations that can expose the immutable bytes used for a manifest digest.</summary>
public interface IExactManifestRepository
{
    ValueTask<byte[]?> GetManifestBytesAsync(PackageId id, PackageVersion version, CancellationToken cancellationToken = default);
}

public interface IFileSetComposer
{
    ValueTask<ComposedFileSet> ComposeAsync(IPackageRepository repository, ResolutionResult resolution, CancellationToken cancellationToken = default);
}
