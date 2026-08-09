namespace NFour.AutoUpdater.Publishing;

public sealed record CoveragePoint
{
    public required string Selection { get; init; }
    public ImmutableArray<string> Packages { get; init; } = [];
    public int FileCount { get; init; }
    public long InstallSize { get; init; }
    public long DownloadSize { get; init; }
    public required ContentHash FileSetId { get; init; }
}
public sealed record CoverageDocument
{
    public int SchemaVersion { get; init; } = 1;
    public required string ReleaseId { get; init; }
    public required string Mode { get; init; }
    public int PointCount { get; init; }
    public ContentHash? Digest { get; init; }
    public ImmutableArray<CoveragePoint> Points { get; init; } = [];
}

public sealed class CoverageGenerator
{
    private const int CoveragePointBudget = 4096;

    public async ValueTask<(CoverageDocument Document, ContentHash Digest)> GenerateAsync(ReleaseLock release, IPackageRepository repository, CancellationToken cancellationToken = default)
    {
        var points = ImmutableArray.CreateBuilder<CoveragePoint>(); var resolver = new VariantResolver(); var composer = new FileSetComposer();
        var exhaustive = SelectionEnumerator.EstimatedCount(release.Axes, CoveragePointBudget + 1) <= CoveragePointBudget;
        // Pairwise enumeration has an explicit bound: it throws instead of producing an
        // incomplete report when the deterministic covering array cannot fit the budget.
        var truncated = false;
        var selections = exhaustive
            ? SelectionEnumerator.Enumerate(release.Axes, CoveragePointBudget)
            : SelectionEnumerator.EnumeratePairwise(release.Axes, CoveragePointBudget, out truncated);
        if (!exhaustive && truncated) throw new InvalidDataException($"Coverage pairwise sample exceeds the {CoveragePointBudget} point budget; refusing to publish an incomplete report.");
        var mode = exhaustive ? "exhaustive" : "sampled";
        foreach (var selection in selections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = resolver.Resolve(release, selection);
            if (!resolved.IsValid) throw new InvalidDataException($"Coverage selection '{selection.ToCanonicalString()}' is invalid: {string.Join("; ", resolved.Diagnostics.Where(x => x.IsError).Select(x => x.Code + " " + x.Message))}");
            var composed = await composer.ComposeAsync(repository, resolved, cancellationToken).ConfigureAwait(false);
            if (!composed.IsValid) throw new InvalidDataException($"Coverage selection '{selection.ToCanonicalString()}' has composition errors: {string.Join("; ", composed.Diagnostics.Where(x => x.IsError).Select(x => x.Code + " " + x.Message))}");
            var uniqueBlobs = new Dictionary<ContentHash, long>();
            foreach (var package in resolved.Packages)
            {
                var manifest = await repository.GetManifestAsync(package.Pin.Id, new PackageVersion(package.Pin.Version.Label, package.Pin.Sequence), cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException($"Manifest '{package.Pin.Id}@{package.Pin.Version}' is missing.");
                await foreach (var entry in repository.ReadFileTableAsync(manifest, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                    if (entry.Kind == FileEntryKind.File) uniqueBlobs.TryAdd(entry.Content, entry.Size);
            }
            points.Add(new CoveragePoint { Selection = selection.ToCanonicalString(), Packages = resolved.Packages.Select(x => x.Id.Value).ToImmutableArray(), FileCount = composed.Files.Count, InstallSize = composed.Files.Values.Sum(x => x.Size), DownloadSize = uniqueBlobs.Values.Sum(), FileSetId = composed.FileSetId });
        }
        var withoutDigest = new CoverageDocument { ReleaseId = release.ReleaseId, Mode = mode, PointCount = points.Count, Points = points.ToImmutable() };
        var digest = ContentHash.Compute(SerializeForDigest(withoutDigest));
        return (withoutDigest with { Digest = digest }, digest);
    }

    public static byte[] Serialize(CoverageDocument document)
    {
        if (document.Digest is not { IsValid: true }) throw new InvalidDataException("Coverage documents written to a repository must carry their digest.");
        return JsonSerializer.SerializeToUtf8Bytes(document, RepositoryJson.Options);
    }

    public static byte[] SerializeForDigest(CoverageDocument document)
    {
        var copy = document with { Digest = null };
        return JsonSerializer.SerializeToUtf8Bytes(copy, RepositoryJson.Options);
    }
}
