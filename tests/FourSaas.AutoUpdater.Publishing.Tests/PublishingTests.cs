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
    public async Task PackageBuilderUsesOnePassDigestsCacheAndValidators()
    {
        var root = Path.Combine(Path.GetTempPath(), "4sup-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "data.json");
        await File.WriteAllTextAsync(source, "{\"ok\":true}");
        var cachePath = Path.Combine(root, "hash-cache.json");
        var package = new SlicedPackage(new PackageId("demo"), [new SlicedFile(source, new VirtualPath("data.json"), FileInstallPolicy.Replace)], []);
        await using var store = new MemoryObjectStore();
        var built = await new PackageBuilder().BuildAsync(package, new PackageVersion("1.0.0", 1), new RepositoryLayout(new RepositoryLayoutTemplates()), store, options: new PackageBuildOptions { HashCachePath = cachePath, Validators = [new JsonContentValidator()] });
        Assert.True(File.Exists(cachePath));
        var entry = (await new StaticRepository(store, new RepositoryDescriptor { RepositoryId = "test", GeneratedAt = DateTimeOffset.UtcNow }).GetManifestAsync(new PackageId("demo"), new PackageVersion("1.0.0", 1)))!;
        var tableEntry = await ReadSingleAsync(new StaticRepository(store, new RepositoryDescriptor { RepositoryId = "test", GeneratedAt = DateTimeOffset.UtcNow }), entry);
        Assert.Equal(ContentHash.Compute("{\"ok\":true}"u8, HashAlgorithmId.Md5), tableEntry.Md5);
        Assert.Equal(1, built.Manifest.FileCount);
        Directory.Delete(root, true);
    }

    [Fact]
    public void SliceSchemaIsGeneratedAndDependenciesAreParsed()
    {
        using var schema = JsonDocument.Parse(SliceRulesYaml.JsonSchema);
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.RootElement.GetProperty("$schema").GetString());
        using var checkedIn = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "schemas", "slice.schema.json")));
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(schema.RootElement.GetRawText()), System.Text.Json.Nodes.JsonNode.Parse(checkedIn.RootElement.GetRawText())), "The embedded authoring schema drifted from the checked-in schema.");
        var rules = SliceRulesYaml.Parse("source: build\npackages:\n  - id: core\n    include: [**/*]\n    requires: [{ id: base, minSequence: 3 }]\nunmatched: error\n");
        Assert.Equal(3, rules.Packages.Single().Requires.Single().MinSequence);
        Assert.Equal("base", rules.Packages.Single().Requires.Single().Id.Value);
    }
    [Fact]
    public void BuildLayoutClassifiesCommonAndAxisTrees()
    {
        var common = BuildTreeLayout.Classify("common/data/world.bin");
        Assert.NotNull(common);
        Assert.Null(common!.Value.Axis);
        Assert.Equal("data/world.bin", common.Value.InstallPath);

        var axis = BuildTreeLayout.Classify("axis/lang/de/data/strings.bin");
        Assert.NotNull(axis);
        Assert.Equal("lang", axis!.Value.Axis);
        Assert.Equal("de", axis.Value.Value);
        Assert.Equal("data/strings.bin", axis.Value.InstallPath);
    }

    [Fact]
    public void BuildLayoutRejectsFilesOutsideTheConvention()
    {
        // The check that keeps the convention true as the build changes. Without it a stray
        // file is merely unclassified, and the first symptom is a package quietly missing
        // content.
        var diagnostics = BuildTreeLayout.Validate(["stray.bin", "axis/lang/orphan.bin", "common/ok.bin"]);
        Assert.Equal(2, diagnostics.Count(x => x.Code == "LAY001"));
        Assert.All(diagnostics.Where(x => x.Code == "LAY001"), x => Assert.True(x.IsError));
    }

    [Fact]
    public void BuildLayoutRejectsTwoAxesClaimingOneInstallPath()
    {
        // Cross-axis disjointness. If lang and ui both own data/shared.bin, switching language
        // rewrites a file ui owns, so a language change drags UI content down with it.
        var diagnostics = BuildTreeLayout.Validate(
        [
            "axis/lang/de/data/shared.bin",
            "axis/ui/classic/data/shared.bin",
        ]);
        var conflict = Assert.Single(diagnostics, x => x.Code == "LAY003");
        Assert.True(conflict.IsError);
        Assert.Contains("data/shared.bin", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildLayoutAllowsValuesOfOneAxisToShareInstallPaths()
    {
        // Values of the same axis are alternatives, never installed together, so sharing an
        // install path is the normal case and must not be reported.
        var diagnostics = BuildTreeLayout.Validate(
        [
            "axis/lang/de/data/strings.bin",
            "axis/lang/en/data/strings.bin",
        ]);
        Assert.DoesNotContain(diagnostics, x => x.IsError);
    }

    [Fact]
    public void BuildLayoutWarnsWhenOneAxisValueIsMissingAFile()
    {
        var diagnostics = BuildTreeLayout.Validate(
        [
            "axis/lang/de/data/strings.bin",
            "axis/lang/de/data/credits.bin",
            "axis/lang/en/data/strings.bin",
        ]);
        var missing = Assert.Single(diagnostics, x => x.Code == "LAY004");
        Assert.False(missing.IsError);
        Assert.Contains("credits.bin", missing.Message, StringComparison.Ordinal);
        Assert.Contains("'en'", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildLayoutRejectsAnUndeclaredAxis()
    {
        var diagnostics = BuildTreeLayout.Validate(["axis/langauge/de/x.bin"], declaredAxes: ["lang", "ui"]);
        var typo = Assert.Single(diagnostics, x => x.Code == "LAY002");
        Assert.True(typo.IsError);
        Assert.Contains("langauge", typo.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildLayoutGeneratesOneRulePerAxisValuePlusBase()
    {
        var rules = BuildTreeLayout.GenerateRules(
        [
            "common/data/world.bin",
            "axis/lang/de/data/strings.bin",
            "axis/lang/en/data/strings.bin",
            "axis/ui/classic/ui/main.bin",
        ]);

        Assert.Equal(["game.base", "lang.de", "lang.en", "ui.classic"], rules.Select(x => x.Id.Value).ToArray());
        var german = rules.Single(x => x.Id.Value == "lang.de");
        Assert.Equal("axis/lang/de", german.StripPrefix);
        Assert.Equal(["axis/lang/de/**"], german.Include);
        // One rule per package regardless of how many files it holds — the property that keeps
        // this maintainable at 200k files.
        Assert.All(rules, rule => Assert.Single(rule.Include));
    }

    [Fact]
    public void StripPrefixMapsAWholeSubtreeWithoutPerFileRules()
    {
        // The point of stripPrefix: one line relocates a subtree of any size. Expressing this
        // with `rewrite` would need an entry per file, which for a 200k-file build is the
        // unmaintainable configuration the layout convention exists to avoid.
        Assert.Equal("data/strings.bin", SlicePaths.Strip("axis/lang/de/data/strings.bin", "axis/lang/de"));
        Assert.Equal("data/strings.bin", SlicePaths.Strip("axis/lang/de/data/strings.bin", "axis/lang/de/"));
        Assert.Equal("data/strings.bin", SlicePaths.Strip("data/strings.bin", null));

        // A path outside the prefix is returned untouched rather than mangled, so a rule can
        // carry a prefix and still match something else without silently corrupting it.
        Assert.Equal("common/data/x.bin", SlicePaths.Strip("common/data/x.bin", "axis/lang/de"));

        // A partial segment match is not a prefix match.
        Assert.Equal("axis/lang/de-AT/x.bin", SlicePaths.Strip("axis/lang/de-AT/x.bin", "axis/lang/de"));
    }

    [Fact]
    public void SliceRulesRejectUnknownAndDuplicateKeys()
    {
        // Rejected rather than skipped: the format looks like YAML, so silently ignoring what
        // it cannot represent would let an editor and the publisher disagree about the file.
        Assert.Throws<FormatException>(() => SliceRulesYaml.Parse("source: build\nnonsense: 1\n"));
        Assert.Throws<FormatException>(() => SliceRulesYaml.Parse("source: build\nsource: other\n"));
        Assert.Throws<FormatException>(() => SliceRulesYaml.Parse(
            "source: build\npackages:\n  - id: core\n    include: [a]\n    include: [b]\n"));
        Assert.Throws<FormatException>(() => SliceRulesYaml.Parse(
            "source: build\npackages:\n  - id: core\n    include: [a]\n    bogus: [b]\n"));
    }

    [Fact]
    public void SliceRulesKeepHashCharactersInsideQuotedValues()
    {
        // Splitting the line on '#' truncated any value containing one, so a glob like
        // "assets/#tmp/**" quietly became "assets/" and changed which files the package claims.
        var rules = SliceRulesYaml.Parse("source: build\npackages:\n  - id: core\n    include: [\"assets/#tmp/**\"]\n");
        Assert.Equal("assets/#tmp/**", rules.Packages.Single().Include.Single());
    }

    [Fact]
    public void ReleaseAuthoringStoresTerminalRetirementMappings()
    {
        var axis = Axis("ui", AxisCardinality.One, "modern") with
        {
            Retired = ImmutableDictionary<string, string>.Empty
                .Add("legacy", "old")
                .Add("old", "modern")
        };

        var release = new ReleaseBuilder().Build(
            "demo", "r1", 1, [axis], [], new Dictionary<PackageId, PackageManifest>(),
            new RepositoryLayout(new RepositoryLayoutTemplates()), DateTimeOffset.UtcNow,
            coverageDigest: ContentHash.Compute("coverage"u8));

        Assert.Equal("modern", release.Axes.Single().Retired["legacy"]);
        Assert.Equal("modern", release.Axes.Single().Retired["old"]);
        Assert.Equal("modern", new ReleaseBuilder().Publish(release).Axes.Single().Retired["legacy"]);
    }

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
        Assert.Equal(3, FileTableSharding.GetShardIndex(new VirtualPath("data/file.bin"), 16));
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

    private static async ValueTask<PackageFileEntry> ReadSingleAsync(IPackageRepository repository, PackageManifest manifest)
    {
        await foreach (var entry in repository.ReadFileTableAsync(manifest)) return entry;
        throw new InvalidDataException("Expected one file-table row.");
    }
}
