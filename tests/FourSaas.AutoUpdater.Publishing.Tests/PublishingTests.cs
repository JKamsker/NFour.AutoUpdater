using FourSaas.AutoUpdater.Core;
using FourSaas.AutoUpdater.Publishing;
using FourSaas.AutoUpdater.Storage;
using FourSaas.AutoUpdater.Storage.Memory;
using FourSaas.AutoUpdater.Repository;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;

namespace FourSaas.AutoUpdater.Publishing.Tests;

public sealed class PublishingTests
{
    [Fact]
    public async Task PublishGateAllowsUnconstrainedDependentWhenDependencyCoversWholeAxis()
    {
        var dependent = new PackageId("dependent");
        var dependency = new PackageId("dependency");
        var dependentManifest = Manifest(dependent, 1, requires: [new PackageDependency { Id = dependency }]);
        var dependencyManifest = Manifest(dependency, 1);
        var release = Release([Axis("region", AxisCardinality.One, "eu", "us")],
        [
            new PackageRequirement { Package = dependent },
            new PackageRequirement { Package = dependency, When = Predicate("region", "eu", "us") }
        ], Pin(dependentManifest), Pin(dependencyManifest));

        var diagnostics = await new PublishGate().CheckAsync(release, ManifestMap(dependentManifest, dependencyManifest));

        Assert.DoesNotContain(diagnostics, x => x.Code == "PKG007");
    }

    [Fact]
    public async Task PublishGateRejectsDependencyThatDoesNotCoverUnconstrainedAxis()
    {
        var dependent = new PackageId("dependent");
        var dependency = new PackageId("dependency");
        var dependentManifest = Manifest(dependent, 1, requires: [new PackageDependency { Id = dependency }]);
        var dependencyManifest = Manifest(dependency, 1);
        var release = Release([Axis("region", AxisCardinality.One, "eu", "us")],
        [
            new PackageRequirement { Package = dependent },
            new PackageRequirement { Package = dependency, When = Predicate("region", "eu") }
        ], Pin(dependentManifest), Pin(dependencyManifest));

        var diagnostics = await new PublishGate().CheckAsync(release, ManifestMap(dependentManifest, dependencyManifest));

        Assert.Contains(diagnostics, x => x.Code == "PKG007");
    }

    [Fact]
    public async Task PublishGateChecksConflictsBeforeDisjointPrefixOptimization()
    {
        var left = new PackageId("left");
        var right = new PackageId("right");
        var leftManifest = Manifest(left, 1, pathPrefixes: ["left/"]);
        var rightManifest = Manifest(right, 1, conflicts: [left], pathPrefixes: ["right/"]);
        var release = Release([], [new PackageRequirement { Package = left }, new PackageRequirement { Package = right }], Pin(leftManifest), Pin(rightManifest));

        var diagnostics = await new PublishGate().CheckAsync(release, ManifestMap(leftManifest, rightManifest));

        Assert.Contains(diagnostics, x => x.Code == "PKG006");
    }

    [Fact]
    public async Task PublishGateRejectsCaseOnlyPathPrefixCollision()
    {
        var manifest = Manifest(new PackageId("case-test"), 1, pathPrefixes: ["Data/", "data/"]);
        var diagnostics = await new PublishGate().CheckAsync(
            Release([], [new PackageRequirement { Package = manifest.Id }], Pin(manifest)),
            ManifestMap(manifest));

        Assert.Contains(diagnostics, x => x.Code == "PKG009");
    }

    [Fact]
    public async Task PublishGateRejectsRequiresAndConflictsDenormalization()
    {
        var left = new PackageId("left");
        var right = new PackageId("right");
        var leftManifest = Manifest(left, 1, requires: [new PackageDependency { Id = right }], conflicts: [right]);
        var rightManifest = Manifest(right, 1);
        var release = Release([], [new PackageRequirement { Package = left }, new PackageRequirement { Package = right }], Pin(leftManifest, requires: [], conflicts: []), Pin(rightManifest));

        var diagnostics = await new PublishGate().CheckAsync(release, ManifestMap(leftManifest, rightManifest));

        Assert.Contains(diagnostics, x => x.Code == "PKG016");
    }

    [Fact]
    public async Task PublishGateConsidersAllCoexistingManyDiscriminatorOutcomes()
    {
        var left = new PackageId("left");
        var right = new PackageId("right");
        var leftManifest = Manifest(left, 1, pathPrefixes: ["shared/"]);
        var rightManifest = Manifest(right, 1, pathPrefixes: ["shared/"]);
        var release = Release([Axis("language", AxisCardinality.Many, "a", "b", "c")],
        [
            new PackageRequirement { Package = left, When = Predicate("language", "a", "b"), LayerOverride = 100 },
            new PackageRequirement { Package = right, When = Predicate("language", "b", "c") }
        ], Pin(leftManifest), Pin(rightManifest));

        var diagnostics = await new PublishGate().CheckAsync(release, ManifestMap(leftManifest, rightManifest));

        // (a,b), (a,c), and (b,b) are all realizable by Many selections.
        Assert.Equal(3, diagnostics.Count(x => x.Code == "PKG002"));
    }

    [Fact]
    public void LargeManyCardinalityIsNotReportedAsExhaustive()
    {
        var axes = ImmutableArray.Create(Axis("language", AxisCardinality.Many, Enumerable.Range(0, 20).Select(x => "v" + x).ToArray()));

        Assert.Equal(4097, SelectionEnumerator.EstimatedCount(axes, 4096));
    }

    [Fact]
    public void PairwiseEnumerationReturnsDeterministicBoundedSample()
    {
        var axes = new[] { Axis("one", AxisCardinality.One, "a", "b"), Axis("two", AxisCardinality.One, "a", "b") }.ToImmutableArray();

        var first = SelectionEnumerator.EnumeratePairwise(axes, 3, out var firstTruncated);
        var second = SelectionEnumerator.EnumeratePairwise(axes, 3, out var secondTruncated);

        Assert.True(firstTruncated);
        Assert.True(secondTruncated);
        Assert.Equal(3, first.Length);
        Assert.Equal(first.Select(x => x.ToCanonicalString()), second.Select(x => x.ToCanonicalString()));
        Assert.Equal(4, SelectionEnumerator.EnumeratePairwise(axes, 4).Length);
    }

    [Fact]
    public async Task CoverageUsesSampledModeForLargeManyAxis()
    {
        await using var store = new MemoryObjectStore();
        await using var repository = new StaticRepository(store, new RepositoryDescriptor { RepositoryId = "test", GeneratedAt = DateTimeOffset.UtcNow });
        var release = Release([Axis("language", AxisCardinality.Many, Enumerable.Range(0, 20).Select(x => "v" + x).ToArray())], []);

        var generated = await new CoverageGenerator().GenerateAsync(release, repository);

        Assert.Equal("sampled", generated.Document.Mode);
        Assert.Equal(generated.Document.Points.Length, generated.Document.PointCount);
    }

    [Fact]
    public async Task PackageBuilderWritesDeduplicatedIdentityBlobsAndReadableShards()
    {
        var root = Path.Combine(Path.GetTempPath(), "4sup-build-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "same"); await File.WriteAllTextAsync(Path.Combine(root, "b.txt"), "same");
        var package = new SlicedPackage(new PackageId("core"), [new SlicedFile(Path.Combine(root, "a.txt"), new VirtualPath("a.txt"), FileInstallPolicy.Replace), new SlicedFile(Path.Combine(root, "b.txt"), new VirtualPath("b.txt"), FileInstallPolicy.Replace)], []);
        await using var store = new MemoryObjectStore(); var layout = new RepositoryLayout(new RepositoryLayoutTemplates()); var published = await new PackageBuilder().BuildAsync(package, new PackageVersion("1.0.0", 1), layout, store);
        Assert.Equal(2, published.Manifest.FileCount); Assert.Single(published.Blobs.Values.Distinct());
        Assert.NotNull(await store.HeadAsync(layout.Package(new PackageId("core"), new PackageVersion("1.0.0", 1))));
        await using var repository = new StaticRepository(store, new RepositoryDescriptor { RepositoryId = "test", GeneratedAt = DateTimeOffset.UtcNow });
        var manifest = await repository.GetManifestAsync(new PackageId("core"), new PackageVersion("1.0.0", 1));
        Assert.NotNull(manifest);
        var rows = new List<PackageFileEntry>(); await foreach (var row in repository.ReadFileTableAsync(manifest!)) rows.Add(row);
        Assert.Equal(2, rows.Count);
        Directory.Delete(root, true);
    }

    [Fact]
    public void ShardDigestIsOrderedByIndex()
    {
        var a = ContentHash.Compute("a"u8); var b = ContentHash.Compute("b"u8);
        var first = FileTableSharding.ComputeTableDigest([new FileTableShardRef { Index = 0, Digest = a, Count = 0, Size = 0 }, new FileTableShardRef { Index = 1, Digest = b, Count = 0, Size = 0 }]);
        var second = FileTableSharding.ComputeTableDigest([new FileTableShardRef { Index = 1, Digest = b, Count = 0, Size = 0 }, new FileTableShardRef { Index = 0, Digest = a, Count = 0, Size = 0 }]);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task CoverageWriterStoresDigestMemberButHashesWithoutIt()
    {
        await using var store = new MemoryObjectStore();
        var descriptor = new RepositoryDescriptor { RepositoryId = "test", GeneratedAt = DateTimeOffset.UtcNow };
        await using var repository = new StaticRepository(store, descriptor);
        var layout = new RepositoryLayout(descriptor.Layout);
        var built = await new ReleaseBuilder().BuildAsync("demo", "r1", 1, [], [], new Dictionary<PackageId, PackageManifest>(), layout, DateTimeOffset.UtcNow, repository);
        await new ReleaseBuilder().WriteCoverageAsync(built.Release, built.Coverage, layout, store);
        var result = await store.OpenAsync(layout.Coverage("demo", "r1"));
        Assert.NotNull(result);
        await using (result!)
        {
            using var bytes = new MemoryStream(); await result.Content.CopyToAsync(bytes);
            using var json = JsonDocument.Parse(bytes.ToArray());
            Assert.Equal(built.CoverageDigest.ToString(), json.RootElement.GetProperty("digest").GetString());
            Assert.Equal(built.CoverageDigest, ContentHash.Compute(CoverageGenerator.SerializeForDigest(built.Coverage)));
        }
    }

    [Fact]
    public async Task StaticProjectionWritesIndexAndByteExactBundle()
    {
        await using var store = new MemoryObjectStore();
        var descriptor = new RepositoryDescriptor { RepositoryId = "test", GeneratedAt = DateTimeOffset.UtcNow };
        var layout = new RepositoryLayout(descriptor.Layout);
        var package = new PackageManifest
        {
            SchemaVersion = 1, Id = new PackageId("core"), Version = new PackageVersion("1.0.0", 1), Sequence = 1,
            Kind = PackageKind.Content, CreatedAt = DateTimeOffset.UtcNow, PathPrefixes = ["bin/"],
            FileTable = new FileTableRef { Format = "jsonl/v1", ShardCount = 1, Digest = ContentHash.Compute("table"u8), Shards = [new FileTableShardRef { Index = 0, Digest = ContentHash.Compute("shard"u8), Count = 0, Size = 0 }] },
            FileCount = 0, InstallSize = 0, DownloadSize = 0
        };
        var pair = Ed25519KeyPair.Create();
        var release = new ReleaseLock { SchemaVersion = 1, ProductId = "demo", ReleaseId = "r1", Sequence = 1, State = ReleaseState.Published, CreatedAt = DateTimeOffset.UtcNow, Axes = [], Requirements = [], Packages = [], CoverageDigest = ContentHash.Compute("coverage"u8) };
        var envelope = SignedDocument.SerializeEnvelope(SignedDocument.Sign("release-lock", SignedDocument.SerializePayload(release), "root", pair.PrivateKey));
        var writer = new StaticProjectionWriter(store, layout);
        await writer.WritePackageIndexAsync(package.Id, [package]);
        await writer.WriteReleaseBundleAsync("demo", "r1", envelope, [package]);

        var index = await store.OpenAsync(layout.PackageIndex(package.Id));
        Assert.NotNull(index);
        await using (index!)
        {
            using var bytes = new MemoryStream(); await index.Content.CopyToAsync(bytes);
            using var json = JsonDocument.Parse(bytes.ToArray());
            Assert.Equal("core", json.RootElement.GetProperty("packageId").GetString());
            Assert.Equal("1.0.0", json.RootElement.GetProperty("versions")[0].GetProperty("version").GetString());
        }
        var bundle = await store.OpenAsync(layout.ReleaseBundle("demo", "r1"));
        Assert.NotNull(bundle);
        await using (bundle!)
        {
            using var bytes = new MemoryStream(); await bundle.Content.CopyToAsync(bytes);
            using var json = JsonDocument.Parse(bytes.ToArray());
            Assert.Equal(Base64Url.Encode(envelope), json.RootElement.GetProperty("lockEnvelope").GetString());
            Assert.True(json.RootElement.GetProperty("inline").TryGetProperty("core", out _));
        }
    }

    private static AxisDefinition Axis(string name, AxisCardinality cardinality, params string[] values) => new()
    {
        Name = name,
        Rank = 1,
        Cardinality = cardinality,
        Required = true,
        Values = values.Select(x => new AxisValue { Id = x }).ToImmutableArray()
    };

    private static AxisPredicate Predicate(string axis, params string[] values) => new()
    {
        Constraints = new Dictionary<string, ImmutableSortedSet<string>>(StringComparer.Ordinal)
        {
            [axis] = values.ToImmutableSortedSet(StringComparer.Ordinal)
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };

    private static ReleaseLock Release(ImmutableArray<AxisDefinition> axes, ImmutableArray<PackageRequirement> requirements, params LockedPackage[] packages) => new()
    {
        SchemaVersion = 1,
        ProductId = "demo",
        ReleaseId = "r1",
        Sequence = 1,
        State = ReleaseState.Draft,
        CreatedAt = DateTimeOffset.UtcNow,
        Axes = axes,
        Requirements = requirements,
        Packages = packages.ToImmutableArray(),
        CoverageDigest = ContentHash.Compute([])
    };

    private static IReadOnlyDictionary<PackageId, PackageManifest> ManifestMap(params PackageManifest[] manifests) =>
        manifests.ToDictionary(x => x.Id, EqualityComparer<PackageId>.Default);

    private static PackageManifest Manifest(PackageId id, long sequence, ImmutableArray<PackageDependency> requires = default, ImmutableArray<PackageId> conflicts = default, ImmutableArray<string> pathPrefixes = default) => new()
    {
        SchemaVersion = 1,
        Id = id,
        Version = new PackageVersion("1.0.0", sequence),
        Sequence = sequence,
        Kind = PackageKind.Content,
        CreatedAt = DateTimeOffset.UtcNow,
        Requires = requires.IsDefault ? [] : requires,
        Conflicts = conflicts.IsDefault ? [] : conflicts,
        PathPrefixes = pathPrefixes.IsDefault ? [] : pathPrefixes,
        FileTable = new FileTableRef
        {
            Format = "jsonl/v1",
            ShardCount = 1,
            Shards = [new FileTableShardRef { Index = 0, Digest = ContentHash.Compute("shard"u8), Count = 0, Size = 0 }],
            Digest = ContentHash.Compute("table"u8)
        },
        FileCount = 0,
        InstallSize = 0,
        DownloadSize = 0
    };

    private static LockedPackage Pin(PackageManifest manifest, ImmutableArray<PackageDependency> requires = default, ImmutableArray<PackageId> conflicts = default) => new()
    {
        Id = manifest.Id,
        Version = manifest.Version,
        Sequence = manifest.Sequence,
        ManifestPath = $"packages/{manifest.Id.Value}/1.0.0/package.json",
        ManifestDigest = ContentHash.Compute(RepositoryJson.SerializeManifest(manifest)),
        FileCount = manifest.FileCount,
        InstallSize = manifest.InstallSize,
        DownloadSize = manifest.DownloadSize,
        Requires = requires.IsDefault ? manifest.Requires : requires,
        Conflicts = conflicts.IsDefault ? manifest.Conflicts : conflicts
    };
}
