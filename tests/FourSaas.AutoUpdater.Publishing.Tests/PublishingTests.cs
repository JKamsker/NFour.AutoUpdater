using FourSaas.AutoUpdater.Core;
using FourSaas.AutoUpdater.Publishing;
using FourSaas.AutoUpdater.Storage;
using FourSaas.AutoUpdater.Storage.Memory;
using FourSaas.AutoUpdater.Repository;
using System.IO;

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
}
