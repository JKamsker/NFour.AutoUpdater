using FourSaas.AutoUpdater.Client;
using FourSaas.AutoUpdater.Core;
using FourSaas.AutoUpdater.Repository;
using FourSaas.AutoUpdater.Storage;
using System.Collections.Immutable;

namespace FourSaas.AutoUpdater.Client.Tests;

public sealed class ClientTests
{
    [Fact]
    public async Task ApplyUsesVerifiedBarrierAndWritesCompactLedger()
    {
        var root = Path.Combine(Path.GetTempPath(), "4sup-apply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var store = new FourSaas.AutoUpdater.Storage.Memory.MemoryObjectStore();
            var layout = new RepositoryLayout(new RepositoryLayoutTemplates());
            var content = "verified-content"u8.ToArray();
            var hash = ContentHash.Compute(content);
            await store.PutAsync(layout.Blob(hash), new MemoryStream(content), content.Length);
            var path = new VirtualPath("bin/game.exe");
            var owner = new PackageId("core");
            var file = new ComposedFile(path, hash, content.Length, owner, FileInstallPolicy.Replace);
            var files = new Dictionary<VirtualPath, ComposedFile> { [path] = file }.ToImmutableSortedDictionary();
            var target = new ComposedFileSet { Files = files, Shadowed = [], FileSetId = FileSetIdentity.Compute(files) };
            var observed = await new LocalTreeScanner().ScanAsync(root, [], [path], HashPolicy.Never);
            var plan = new InstallPlanner().Plan(target, null, observed);
            var installLock = new InstallLock { RepositoryUri = "memory://test", ProductId = "product", Channel = "live", ReleaseId = "r1", ReleaseDigest = ContentHash.Compute("release"u8), Selection = new VariantSelection { Axes = ImmutableSortedDictionary<string, ImmutableSortedSet<string>>.Empty }, SelectionId = ContentHash.Compute([]), FileSetId = target.FileSetId, AppliedAt = DateTimeOffset.UtcNow };
            await new InstallApplier().ApplyAsync(root, plan, target, installLock, store, layout, new InstallLedger(root));
            Assert.Equal("verified-content", await File.ReadAllTextAsync(Path.Combine(root, "bin", "game.exe")));
            var ledgerText = await File.ReadAllTextAsync(Path.Combine(root, ".4sup", "state.jsonl"));
            Assert.Contains("\"k\":\"lock\"", ledgerText, StringComparison.Ordinal);
            Assert.NotNull(await new InstallLedger(root).ReadAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void FreshPlanNeverDeletesUnknownFiles()
    {
        var path = new VirtualPath("bin/game.exe"); var targetFile = new ComposedFile(path, ContentHash.Compute("game"u8), 4, new PackageId("core"), FileInstallPolicy.Replace);
        var target = new ComposedFileSet { Files = new Dictionary<VirtualPath, ComposedFile> { [path] = targetFile }.ToImmutableSortedDictionary(), Shadowed = [], FileSetId = ContentHash.Compute([]) };
        var observed = new ObservedTreeSnapshot("/install", ImmutableDictionary<VirtualPath, ObservedEntry>.Empty);
        var plan = new InstallPlanner().Plan(target, null, observed);
        Assert.DoesNotContain(plan.Operations, x => x is FileOperation.Delete);
    }

    [Fact]
    public void PreserveFilesBecomeOrphansInsteadOfDeletes()
    {
        var path = new VirtualPath("config/user.ini"); var current = new Dictionary<VirtualPath, InstalledFile> { [path] = new InstalledFile(path, ContentHash.Compute("old"u8), 3, new PackageId("core"), FileInstallPolicy.Preserve, 3, 1) };
        var target = new ComposedFileSet { Files = ImmutableSortedDictionary<VirtualPath, ComposedFile>.Empty, Shadowed = [], FileSetId = ContentHash.Compute([]) };
        var plan = new InstallPlanner().Plan(target, current, new ObservedTreeSnapshot("/install", ImmutableDictionary<VirtualPath, ObservedEntry>.Empty));
        Assert.Contains(plan.Operations, x => x is FileOperation.Orphan);
        Assert.DoesNotContain(plan.Operations, x => x is FileOperation.Delete);
    }

    [Fact]
    public async Task MirrorFailoverDiscardsPoisonedPartialContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "4sup-mirror-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var bad = new FourSaas.AutoUpdater.Storage.Memory.MemoryObjectStore();
            await using var good = new FourSaas.AutoUpdater.Storage.Memory.MemoryObjectStore();
            var expectedBytes = "correct"u8.ToArray(); var expected = ContentHash.Compute(expectedBytes); var key = new ObjectKey("blobs/sha256/00/00/" + Convert.ToHexString(expected.Value.Span).ToLowerInvariant());
            await bad.PutAsync(key, new MemoryStream("poisoned"u8.ToArray()));
            await good.PutAsync(key, new MemoryStream(expectedBytes));
            var staging = Path.Combine(root, "staging");
            var length = await new BlobFetcher().FetchFromMirrorsAsync([bad, good], key, expected, staging);
            Assert.Equal(expectedBytes.Length, length);
            Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(staging));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LedgerReadDoesNotCreateInstallMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "4sup-readonly-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(await new InstallLedger(root).ReadAsync());
            Assert.False(Directory.Exists(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ApplyRejectsAReparsePointInAManagedParent()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "4sup-reparse-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "4sup-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "bin"), outside);
            await using var store = new FourSaas.AutoUpdater.Storage.Memory.MemoryObjectStore();
            var layout = new RepositoryLayout(new RepositoryLayoutTemplates());
            var content = "must-not-land-outside"u8.ToArray(); var hash = ContentHash.Compute(content);
            await store.PutAsync(layout.Blob(hash), new MemoryStream(content), content.Length);
            var path = new VirtualPath("bin/game.exe"); var file = new ComposedFile(path, hash, content.Length, new PackageId("core"), FileInstallPolicy.Replace);
            var files = new Dictionary<VirtualPath, ComposedFile> { [path] = file }.ToImmutableSortedDictionary();
            var target = new ComposedFileSet { Files = files, Shadowed = [], FileSetId = FileSetIdentity.Compute(files) };
            var observed = await new LocalTreeScanner().ScanAsync(root, [], [path], HashPolicy.Never);
            var plan = new InstallPlanner().Plan(target, null, observed);
            var installLock = new InstallLock { RepositoryUri = "memory://test", ProductId = "product", Channel = "live", ReleaseId = "r1", ReleaseDigest = ContentHash.Compute("release"u8), Selection = new VariantSelection { Axes = ImmutableSortedDictionary<string, ImmutableSortedSet<string>>.Empty }, SelectionId = ContentHash.Compute([]), FileSetId = target.FileSetId, AppliedAt = DateTimeOffset.UtcNow };
            await Assert.ThrowsAsync<IOException>(async () => await new InstallApplier().ApplyAsync(root, plan, target, installLock, store, layout, new InstallLedger(root)));
            Assert.False(File.Exists(Path.Combine(outside, "game.exe")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); if (Directory.Exists(outside)) Directory.Delete(outside, true); }
    }

    [Fact]
    public async Task CacheMutationIsRejectedBeforeMaterialisation()
    {
        var root = Path.Combine(Path.GetTempPath(), "4sup-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cache = new LocalContentCache(root);
            var hash = ContentHash.Compute("original"u8);
            await cache.StoreAsync(hash, new MemoryStream("original"u8.ToArray()));
            File.SetAttributes(cache.GetPath(hash), FileAttributes.Normal);
            await File.WriteAllBytesAsync(cache.GetPath(hash), "tampered"u8.ToArray());
            Assert.False(await cache.TryGetAsync(hash));
            Assert.False(File.Exists(cache.GetPath(hash)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
