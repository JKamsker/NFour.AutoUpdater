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
        Assert.Equal("sha256:fe78c2fb21bd31e7664062f8bd57af722421d7beb7550e8ddff8e5db840e9ba4", target.FileSetId.ToString());
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

    [Fact]
    public void DetachedEnvelopeVerifiesBeforeParsingPayload()
    {
        var pair = Ed25519KeyPair.Create();
        var envelope = SignedDocument.Sign("channel-pointer", Encoding.UTF8.GetBytes("{\"message\":\"ok\"}"), "test", pair.PrivateKey);
        var tampered = envelope with { Payload = Base64Url.Encode(Encoding.UTF8.GetBytes("{\"a\":1,\"a\":2}")) };

        Assert.False(SignedDocument.Verify(tampered, new Dictionary<string, byte[]> { ["test"] = pair.PublicKey }, out _, out var error));
        Assert.Contains("No signature matched", error);
    }

    [Fact]
    public void DetachedEnvelopeHonoursSigningKeyValidityWindow()
    {
        var pair = Ed25519KeyPair.Create();
        var envelope = SignedDocument.Sign("channel-pointer", Encoding.UTF8.GetBytes("{\"message\":\"ok\"}"), "test", pair.PrivateKey);
        var keys = new Dictionary<string, VerificationKey> { ["test"] = new(pair.PublicKey, DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1)) };
        Assert.False(SignedDocument.Verify(envelope, keys, out _, out var error, DateTimeOffset.UtcNow));
        Assert.Contains("validity window", error);
    }

    [Fact]
    public void DetachedEnvelopeUsesTheDocumentTimestampForRotationWindows()
    {
        var pair = Ed25519KeyPair.Create();
        var created = DateTimeOffset.UtcNow.AddDays(-2);
        var payload = SignedDocument.SerializePayload(new ReleaseLock
        {
            SchemaVersion = 1, ProductId = "product", ReleaseId = "r1", Sequence = 1, State = ReleaseState.Published,
            CreatedAt = created, Axes = [], Requirements = [], Packages = [], CoverageDigest = ContentHash.Compute([])
        });
        var envelope = SignedDocument.Sign("release-lock", payload, "rotated", pair.PrivateKey);
        var keys = new Dictionary<string, VerificationKey> { ["rotated"] = new(pair.PublicKey, created.AddHours(-1), created.AddHours(1)) };
        Assert.True(SignedDocument.VerifyCryptographically(envelope, keys, out var verified, out var signer, out var error), error);
        var release = SignedDocument.DeserializePayload<ReleaseLock>(verified);
        Assert.Equal("rotated", signer);
        Assert.True(SignedDocument.IsKeyValidAt(keys, signer!, release.CreatedAt, out error), error);
    }

    [Fact]
    public void KeyManifestRotationRequiresOverlapAndNeverLeavesTrustEmpty()
    {
        var first = Ed25519KeyPair.Create();
        var second = Ed25519KeyPair.Create();
        var now = DateTimeOffset.UtcNow;
        var current = new KeyManifest
        {
            KeySequence = 1,
            Keys = [new PublicKeyRecord { KeyId = "root", Algorithm = "ed25519", PublicKey = Base64Url.Encode(first.PublicKey), NotBefore = now.AddDays(-1), NotAfter = now.AddDays(1) }]
        };
        var rotated = current with
        {
            KeySequence = 2,
            Keys = [
                current.Keys[0],
                new PublicKeyRecord { KeyId = "next", Algorithm = "ed25519", PublicKey = Base64Url.Encode(second.PublicKey), NotBefore = now.AddHours(-1), NotAfter = now.AddDays(2) }
            ]
        };
        Assert.True(ControlDocumentPolicy.ValidateKeyManifest(rotated, new HashSet<string>(["root"], StringComparer.Ordinal), "root", now, TimeSpan.FromHours(1), out var error), error);
        var substituted = rotated with { Keys = [rotated.Keys[0] with { PublicKey = Base64Url.Encode(second.PublicKey) }, rotated.Keys[1]] };
        Assert.False(ControlDocumentPolicy.ValidateKeyManifest(substituted, new Dictionary<string, byte[]> { ["root"] = first.PublicKey }, "root", now, TimeSpan.FromHours(1), out _));
        var empty = rotated with { RevokedKeyIds = ["root", "next"] };
        Assert.False(ControlDocumentPolicy.ValidateKeyManifest(empty, new HashSet<string>(["root"], StringComparer.Ordinal), "root", now, TimeSpan.FromHours(1), out _));
    }

    [Fact]
    public void ChannelRollbackIsAcceptedOnlyAsAForwardChannelSequence()
    {
        var now = DateTimeOffset.UtcNow;
        var rollback = new ChannelPointer
        {
            ProductId = "product", Channel = "live", ChannelSequence = 419, SupersedesChannelSequence = 418,
            ReleaseId = "r417", ReleaseSequence = 417, ReleaseDigest = ContentHash.Compute("r417"u8), UpdatedAt = now
        };
        var accepted = ControlDocumentPolicy.AcceptChannel(rollback, "product", "live", 418, now, TimeSpan.FromDays(1), 418, new Version(1, 0, 0));
        Assert.True(accepted.Accepted, accepted.Error);
        Assert.True(accepted.IsRollback);

        var restored = rollback with { ChannelSequence = 418, SupersedesChannelSequence = 417 };
        var rejected = ControlDocumentPolicy.AcceptChannel(restored, "product", "live", 418, now, TimeSpan.FromDays(1), 418, new Version(1, 0, 0));
        Assert.False(rejected.Accepted);
    }

    [Theory]
    [InlineData("0644", 0x1A4)]
    [InlineData("0755", 0x1ED)]
    [InlineData("0600", 0x180)]
    public void FileModesParseTheirOctalPermissionBits(string text, uint expected)
    {
        Assert.True(PosixFileMode.TryParse(text, out var mode, out var error), error);
        Assert.Equal(expected, mode);
        Assert.Equal(text, PosixFileMode.Format(mode));
    }

    [Theory]
    [InlineData("4755")] // setuid
    [InlineData("2755")] // setgid
    [InlineData("1777")] // sticky
    public void FileModesRejectSetuidSetgidAndSticky(string text)
    {
        Assert.False(PosixFileMode.TryParse(text, out _, out var error));
        Assert.Contains("setuid", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("755")]    // too short
    [InlineData("00755")]  // too long
    [InlineData("0778")]   // not octal
    [InlineData("07x5")]
    [InlineData(null)]
    public void FileModesRejectAnythingOutsideTheFourDigitOctalGrammar(string? text)
    {
        Assert.False(PosixFileMode.TryParse(text, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void FileModeResolutionPrefersTheDeclaredModeOverThePolicyDefault()
    {
        Assert.Equal(0x1C0u, PosixFileMode.Resolve("0700", FileInstallPolicy.Executable));
        Assert.Equal(PosixFileMode.DefaultExecutable, PosixFileMode.Resolve(null, FileInstallPolicy.Executable));
        Assert.Equal(PosixFileMode.DefaultRegular, PosixFileMode.Resolve(null, FileInstallPolicy.Replace));
    }

    private static PackageId Id(string value) => new(value);
    private static LockedPackage Pin(PackageId id) => new() { Id = id, Version = new PackageVersion("1.0.0", 1), ManifestPath = "", ManifestDigest = ContentHash.Compute([]), FileCount = 0, InstallSize = 0, DownloadSize = 0 };
}
