namespace FourSaas.AutoUpdater.Publishing;

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
    public async ValueTask<(CoverageDocument Document, ContentHash Digest)> GenerateAsync(ReleaseLock release, IPackageRepository repository, CancellationToken cancellationToken = default)
    {
        var points = ImmutableArray.CreateBuilder<CoveragePoint>(); var resolver = new VariantResolver(); var composer = new FileSetComposer();
        var selections = SelectionEnumerator.Enumerate(release.Axes, 4096); var mode = selections.Length < 4096 ? "exhaustive" : "sampled";
        foreach (var selection in selections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = resolver.Resolve(release, selection);
            if (!resolved.IsValid) continue;
            var composed = await composer.ComposeAsync(repository, resolved, cancellationToken).ConfigureAwait(false);
            points.Add(new CoveragePoint { Selection = selection.ToCanonicalString(), Packages = resolved.Packages.Select(x => x.Id.Value).ToImmutableArray(), FileCount = composed.Files.Count, InstallSize = composed.Files.Values.Sum(x => x.Size), DownloadSize = resolved.Packages.Sum(x => x.Pin.DownloadSize), FileSetId = composed.FileSetId });
        }
        var withoutDigest = new CoverageDocument { ReleaseId = release.ReleaseId, Mode = mode, PointCount = points.Count, Points = points.ToImmutable() };
        var digest = ContentHash.Compute(Serialize(withoutDigest));
        return (withoutDigest with { Digest = digest }, digest);
    }

    public static byte[] Serialize(CoverageDocument document)
    {
        var copy = document with { Digest = null };
        return JsonSerializer.SerializeToUtf8Bytes(copy, RepositoryJson.Options);
    }
}
