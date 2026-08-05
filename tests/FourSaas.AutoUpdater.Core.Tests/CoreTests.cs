using FourSaas.AutoUpdater.Core;
using System.Collections.Immutable;
using System.Text;

namespace FourSaas.AutoUpdater.Core.Tests;

public sealed class CoreTests
{
    [Fact]
    public void ContentHashIsStructuralAndRoundTrips()
    {
        var bytes = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
        var first = new ContentHash(HashAlgorithmId.Sha256, bytes);
        var second = new ContentHash(HashAlgorithmId.Sha256, bytes.ToArray());
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal(first, ContentHash.Parse(first.ToString()));
        bytes[0] = 255;
        Assert.NotEqual(first, new ContentHash(HashAlgorithmId.Sha256, bytes));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("data/aux.dat")]
    [InlineData("data/file. ")]
    [InlineData("/absolute")]
    [InlineData("C:/drive")]
    public void VirtualPathRejectsNonPortablePaths(string value) => Assert.False(VirtualPath.TryCreate(value, out _, out _));

    [Fact]
    public void SelectionCanonicalisationIsStable()
    {
        var selection = new VariantSelection { Axes = new Dictionary<string, ImmutableSortedSet<string>>(StringComparer.Ordinal) { ["ui"] = ImmutableSortedSet.Create(StringComparer.Ordinal, "modern"), ["language"] = ImmutableSortedSet.Create(StringComparer.Ordinal, "en", "de") }.ToImmutableSortedDictionary(StringComparer.Ordinal) };
        Assert.Equal("language=de,en;ui=modern", selection.ToCanonicalString());
        Assert.Equal(selection.SelectionId, ContentHash.Compute(Encoding.UTF8.GetBytes("language=de,en;ui=modern")));
    }

    [Fact]
    public void ResolverDerivesLayersAndAllowsDeclaredOverride()
    {
        var core = Id("core"); var ui = Id("ui.modern");
        var release = new ReleaseLock { SchemaVersion = 1, ProductId = "product", ReleaseId = "r1", Sequence = 1, State = ReleaseState.Published, CreatedAt = DateTimeOffset.UtcNow, CoverageDigest = ContentHash.Compute([]), Axes = [new AxisDefinition { Name = "ui", Rank = 20, Cardinality = AxisCardinality.One, Required = true, Default = "modern", Values = [new AxisValue { Id = "modern" }] }], Requirements = [new PackageRequirement { Package = core }, new PackageRequirement { Package = ui, When = new AxisPredicate { Constraints = new Dictionary<string, ImmutableSortedSet<string>>(StringComparer.Ordinal) { ["ui"] = ImmutableSortedSet.Create(StringComparer.Ordinal, "modern") }.ToImmutableSortedDictionary(StringComparer.Ordinal), }, Overrides = [core] }], Packages = [Pin(core), Pin(ui)] };
        var result = new VariantResolver().Resolve(release, new VariantSelection { Axes = ImmutableSortedDictionary<string, ImmutableSortedSet<string>>.Empty });
        Assert.True(result.IsValid, string.Join('\n', result.Diagnostics));
        Assert.Equal([0, 20000], result.Packages.Select(x => x.Layer).ToArray());
    }

    [Fact]
    public void LostLedgerNeverProducesDeletes()
    {
        var path = new VirtualPath("data/file.bin"); var file = new ComposedFile(path, ContentHash.Compute("x"u8), 1, Id("core"), FileInstallPolicy.Replace);
        var target = new ComposedFileSet { Files = new Dictionary<VirtualPath, ComposedFile> { [path] = file }.ToImmutableSortedDictionary(), Shadowed = [], FileSetId = FileSetIdentity.Compute(new Dictionary<VirtualPath, ComposedFile> { [path] = file }) };
        var preserve = target with { Files = new Dictionary<VirtualPath, ComposedFile> { [path] = file with { Policy = FileInstallPolicy.Preserve } }.ToImmutableSortedDictionary() };
        Assert.NotEqual(target.FileSetId, FileSetIdentity.Compute(preserve.Files));
    }

    [Fact]
    public void DetachedEnvelopeSignsExactPayloadAndRejectsDuplicateJson()
    {
        var pair = Ed25519KeyPair.Create(); var payload = Encoding.UTF8.GetBytes("{\"message\":\"héllo\"}");
        var envelope = SignedDocument.Sign("channel-pointer", payload, "test", pair.PrivateKey);
        Assert.True(SignedDocument.Verify(envelope, new Dictionary<string, byte[]> { ["test"] = pair.PublicKey }, out var verified, out var error), error);
        Assert.Equal(payload, verified);
        var duplicate = SignedDocument.Sign("channel-pointer", Encoding.UTF8.GetBytes("{\"a\":1,\"a\":2}"), "test", pair.PrivateKey);
        Assert.False(SignedDocument.Verify(duplicate, new Dictionary<string, byte[]> { ["test"] = pair.PublicKey }, out _, out _));
    }

    private static PackageId Id(string value) => new(value);
    private static LockedPackage Pin(PackageId id) => new() { Id = id, Version = new PackageVersion("1.0.0", 1), ManifestPath = "", ManifestDigest = ContentHash.Compute([]), FileCount = 0, InstallSize = 0, DownloadSize = 0 };
}
