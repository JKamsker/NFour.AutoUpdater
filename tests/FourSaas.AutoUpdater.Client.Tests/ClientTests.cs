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
}
