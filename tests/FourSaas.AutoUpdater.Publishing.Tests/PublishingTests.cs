using FourSaas.AutoUpdater.Core;
using FourSaas.AutoUpdater.Publishing;
using FourSaas.AutoUpdater.Storage;
using FourSaas.AutoUpdater.Storage.Memory;
using FourSaas.AutoUpdater.Repository;
using System.IO;
using System.Text.Json;

namespace FourSaas.AutoUpdater.Publishing.Tests;

public sealed class PublishingTests
{
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
}
