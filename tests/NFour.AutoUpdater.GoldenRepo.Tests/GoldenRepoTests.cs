using NFour.AutoUpdater.Core;
using NFour.AutoUpdater.Repository;
using NFour.AutoUpdater.Storage;
using NFour.AutoUpdater.Storage.Memory;
using Json.Schema;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Xunit;

namespace NFour.AutoUpdater.GoldenRepo.Tests;

public sealed class GoldenRepoTests
{
    [Fact]
    public async Task DryRunReportsTheExactOrphanDeletionSet()
    {
        await using var store = new MemoryObjectStore();
        var layout = new RepositoryLayout(new RepositoryLayoutTemplates());
        var live = ContentHash.Compute("live"u8);
        var orphan = ContentHash.Compute("orphan"u8);
        await store.PutAsync(layout.Blob(live), new MemoryStream("live"u8.ToArray()));
        await store.PutAsync(layout.Blob(orphan), new MemoryStream("orphan"u8.ToArray()));

        var now = new FixedTimeProvider(DateTimeOffset.UtcNow.AddDays(2));
        var result = await new GarbageCollector().CollectAsync(store, layout, [live], new GarbageCollectionOptions { DryRun = true }, now);

        Assert.Equal([layout.Blob(orphan)], result.Deleted);
        Assert.Empty(result.Quarantined);
    }

    [Fact]
    public async Task CheckedInGoldenTreeCollectsItsDeliberateOrphan()
    {
        await using var store = await LoadGoldenStoreAsync();
        var descriptor = new RepositoryDescriptor { RepositoryId = "golden-v1", GeneratedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z") };
        var layout = new RepositoryLayout(descriptor.Layout);
        await using var repository = new StaticRepository(store, descriptor);
        var trusted = new Dictionary<string, byte[]>
        {
            ["fixture-root"] = Base64Url.Decode("ebVWLo_mVPlAeLES6KmLp5AfhTrmlb7X4OORC60ElmQ")
        };
        var reader = new RepositoryReader(store, descriptor);
        var alphaChannel = await reader.ReadChannelAsync("alpha.product", "live", trusted);
        var betaChannel = await reader.ReadChannelAsync("beta.product", "live", trusted);
        var alpha = await reader.ReadReleaseAsync("alpha.product", alphaChannel.Pointer.ReleaseId, trusted, alphaChannel.Pointer.ReleaseDigest);
        var beta = await reader.ReadReleaseAsync("beta.product", betaChannel.Pointer.ReleaseId, trusted, betaChannel.Pointer.ReleaseDigest);

        var result = await new GarbageCollector().CollectLiveAsync(
            store, layout, [alpha.Lock, beta.Lock], repository,
            new GarbageCollectionOptions { DryRun = true },
            new FixedTimeProvider(DateTimeOffset.UtcNow.AddDays(2)));

        Assert.Contains(layout.Blob(ContentHash.Parse("sha256:e70491a42942db28d2e54d9d0116bd1b477953f9ecdb4e8e9ce690466a00c661")), result.Deleted);

        var collected = await new GarbageCollector().CollectLiveAsync(
            store, layout, [alpha.Lock, beta.Lock], repository,
            new GarbageCollectionOptions { MinimumBlobAge = TimeSpan.Zero },
            new FixedTimeProvider(DateTimeOffset.UtcNow.AddDays(2)));

        var orphanKey = layout.Blob(ContentHash.Parse("sha256:e70491a42942db28d2e54d9d0116bd1b477953f9ecdb4e8e9ce690466a00c661"));
        Assert.Contains(orphanKey, collected.Deleted);
        Assert.Contains(collected.Quarantined, key => key.Value.EndsWith('/' + orphanKey.Value.Split('/').Last(), StringComparison.Ordinal));
        Assert.Null(await store.HeadAsync(orphanKey));
    }

    [Fact]
    public void LayoutRejectsCaseAmbiguousIdentifiers()
    {
        Assert.Throws<ArgumentException>(() => new PackageId("UPPER"));
    }

    [Fact]
    public void SignedVectorsVerifyAndRejectDuplicatePayloadProperties()
    {
        using var keyVector = JsonDocument.Parse(File.ReadAllBytes(VectorPath("vector-key.json")));
        var keyId = keyVector.RootElement.GetProperty("keyId").GetString()!;
        var key = Base64Url.Decode(keyVector.RootElement.GetProperty("publicKey").GetString()!);
        var trusted = new Dictionary<string, byte[]> { [keyId] = key };
        var validBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "vectors", "signed", "valid-envelope.json"));
        var valid = SignedDocument.DeserializeEnvelope(validBytes);
        Assert.Equal("M5lGz-5zebLNq3BU_yhCUYT8mVYw5oNTdExn0Hc0WfhuyOrIlTjTYlkA6HiDBrDnasnFi8gTmwmN9EzDGMGoAw", valid.Signatures.Single().Signature);
        Assert.True(SignedDocument.Verify(valid, trusted, out var validPayload, out var validError), validError);
        Assert.Equal("{\"message\": \"héllo\",\n \"number\": -7}", Encoding.UTF8.GetString(Base64Url.Decode(valid.Payload)));
        JsonRules.Validate(validPayload);

        var duplicateBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "vectors", "signed", "duplicate-envelope.json"));
        var duplicate = SignedDocument.DeserializeEnvelope(duplicateBytes);
        Assert.False(SignedDocument.Verify(duplicate, trusted, out _, out var duplicateError));
        Assert.Contains("Duplicate JSON", duplicateError, StringComparison.Ordinal);

        using var duplicateVector = JsonDocument.Parse(File.ReadAllBytes(VectorPath("duplicate-key.json")));
        Assert.Equal("reject", duplicateVector.RootElement.GetProperty("expected").GetString());
        Assert.Throws<FormatException>(() => JsonRules.Validate(Encoding.UTF8.GetBytes(duplicateVector.RootElement.GetProperty("payload").GetString()!)));
    }

    [Fact]
    public void SignedLifecycleVectorsExecuteRollbackRotationAndRevocationPolicies()
    {
        using (var rollbackDocument = JsonDocument.Parse(File.ReadAllBytes(VectorPath("rollback-chain.json"))))
        {
            long lastChannel = 0;
            long? lastRelease = null;
            foreach (var item in rollbackDocument.RootElement.GetProperty("sequence").EnumerateArray())
            {
                var pointer = new ChannelPointer
                {
                    ProductId = "demo", Channel = "live",
                    ChannelSequence = item.GetProperty("channelSequence").GetInt64(),
                    SupersedesChannelSequence = item.GetProperty("supersedesChannelSequence").GetInt64(),
                    ReleaseId = item.GetProperty("releaseId").GetString()!,
                    ReleaseSequence = item.GetProperty("releaseSequence").GetInt64(),
                    ReleaseDigest = ContentHash.Compute(Encoding.UTF8.GetBytes(item.GetProperty("releaseId").GetString()!)),
                    UpdatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
                };
                var result = ControlDocumentPolicy.AcceptChannel(pointer, "demo", "live", lastChannel, pointer.UpdatedAt, TimeSpan.FromDays(1), lastRelease, new Version(1, 0, 0));
                Assert.Equal(item.GetProperty("accepted").GetBoolean(), result.Accepted);
                Assert.Equal(item.GetProperty("rollback").GetBoolean(), result.IsRollback);
                if (result.Accepted) { lastChannel = pointer.ChannelSequence; lastRelease = pointer.ReleaseSequence; }
            }
        }

        using (var rotationDocument = JsonDocument.Parse(File.ReadAllBytes(VectorPath("key-rotation.json"))))
        {
            var oldId = rotationDocument.RootElement.GetProperty("keys")[0].GetProperty("keyId").GetString()!;
            var newId = rotationDocument.RootElement.GetProperty("keys")[1].GetProperty("keyId").GetString()!;
            var oldKey = FixtureKey(1);
            var newKey = FixtureKey(33);
            var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            var oldRecord = new PublicKeyRecord { KeyId = oldId, Algorithm = "ed25519", PublicKey = Base64Url.Encode(oldKey.PublicKey), NotBefore = now.AddDays(-1), NotAfter = now.AddDays(1) };
            var newRecord = new PublicKeyRecord { KeyId = newId, Algorithm = "ed25519", PublicKey = Base64Url.Encode(newKey.PublicKey), NotBefore = now.AddHours(-1), NotAfter = now.AddDays(2) };
            var rotated = new KeyManifest { KeySequence = rotationDocument.RootElement.GetProperty("keySequence").GetInt64(), Keys = [oldRecord, newRecord] };
            Assert.True(ControlDocumentPolicy.ValidateKeyManifest(rotated, new HashSet<string>([oldId], StringComparer.Ordinal), oldId, now, TimeSpan.FromHours(1), out var rotationError), rotationError);
            var oldEnvelope = SignedDocument.Sign("channel-pointer", Encoding.UTF8.GetBytes("{\"message\":\"signed-before-rotation\"}"), oldId, oldKey.PrivateKey);
            Assert.True(SignedDocument.Verify(oldEnvelope, new Dictionary<string, byte[]> { [oldId] = oldKey.PublicKey, [newId] = newKey.PublicKey }, out _, out var verifyError), verifyError);
        }

        using (var revocationDocument = JsonDocument.Parse(File.ReadAllBytes(VectorPath("revocation-empty-trust.json"))))
        {
            var ids = revocationDocument.RootElement.GetProperty("revokedKeyIds").EnumerateArray().Select(x => x.GetString()!).ToArray();
            var now = DateTimeOffset.UtcNow;
            var keys = ids.Select((id, index) => new PublicKeyRecord { KeyId = id, Algorithm = "ed25519", PublicKey = Base64Url.Encode(FixtureKey((byte)(60 + index)).PublicKey), NotBefore = now.AddDays(-1), NotAfter = now.AddDays(1) }).ToImmutableArray();
            var current = new KeyManifest { KeySequence = 1, Keys = keys };
            var candidate = current with { KeySequence = 2, RevokedKeyIds = ids.ToImmutableArray() };
            Assert.False(ControlDocumentPolicy.CanApplyRevocations(current, candidate, out _ , now));
            Assert.False(ControlDocumentPolicy.ValidateKeyManifest(candidate, new HashSet<string>(ids, StringComparer.Ordinal), ids[0], now, TimeSpan.FromHours(1), out _));
        }

        var valid = SignedDocument.DeserializeEnvelope(File.ReadAllBytes(VectorPath("valid-envelope.json")));
        var wrongType = valid with { Type = "release-lock" };
        Assert.False(SignedDocument.Verify(wrongType, new Dictionary<string, byte[]> { ["vector-key"] = Base64Url.Decode("A6EHv_POEL4dcN0Y50vAmWfk1jCbpQ1fHdyGZBJVMbg") }, out _, out _));
    }

    [Fact]
    public void SignedPayloadShapeVectorPreservesOpaquePayloadBytes()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(VectorPath("payload-shapes.json")));
        var key = FixtureKey(90);
        foreach (var payload in document.RootElement.GetProperty("payloads").EnumerateArray())
        {
            var bytes = Encoding.UTF8.GetBytes(payload.GetString()!);
            var envelope = SignedDocument.Sign("channel-pointer", bytes, "shape", key.PrivateKey);
            Assert.True(SignedDocument.Verify(envelope, new Dictionary<string, byte[]> { ["shape"] = key.PublicKey }, out var verified, out var error), error);
            Assert.Equal(bytes, verified);
        }

        using var whitespace = JsonDocument.Parse(File.ReadAllBytes(VectorPath("whitespace-payload.json")));
        var whitespaceBytes = Encoding.UTF8.GetBytes(whitespace.RootElement.GetProperty("payload").GetString()!.Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\\"", "\"", StringComparison.Ordinal));
        var whitespaceKey = FixtureKey(91);
        var whitespaceEnvelope = SignedDocument.Sign("channel-pointer", whitespaceBytes, "shape-whitespace", whitespaceKey.PrivateKey);
        Assert.True(SignedDocument.Verify(whitespaceEnvelope, new Dictionary<string, byte[]> { ["shape-whitespace"] = whitespaceKey.PublicKey }, out var verifiedWhitespace, out var whitespaceError), whitespaceError);
        Assert.Equal(whitespaceBytes, verifiedWhitespace);
    }

    [Fact]
    public async Task CheckedInGoldenTreeLoadsAllProductsAndResolvesSelectionPoints()
    {
        await using var store = await LoadGoldenStoreAsync();
        var (descriptor, layout) = await RepositoryFactory.LoadDescriptorAsync(store);
        Assert.Equal(["alpha.product", "beta.product"], descriptor.Products);
        using var catalog = JsonDocument.Parse(await ReadObjectAsync(store, new ObjectKey("golden-catalog.json")));
        Assert.Equal(2, catalog.RootElement.GetProperty("products").GetArrayLength());
        Assert.Equal(3, catalog.RootElement.GetProperty("products").EnumerateArray().Sum(x => x.GetProperty("releases").GetArrayLength()));
        Assert.Equal(5, catalog.RootElement.GetProperty("axes").GetArrayLength());

        await using var repository = new StaticRepository(store, descriptor);
        var fixtureRoot = Base64Url.Decode("ebVWLo_mVPlAeLES6KmLp5AfhTrmlb7X4OORC60ElmQ");
        var trusted = new Dictionary<string, byte[]> { ["fixture-root"] = fixtureRoot };
        var reader = new RepositoryReader(store, descriptor);
        var fixtureNow = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var keys = await reader.ReadKeyManifestAsync(trusted, now: fixtureNow);
        Assert.Equal(1, keys.Manifest.KeySequence);
        Assert.Contains(keys.Manifest.Keys, x => x.KeyId == "fixture-root");
        foreach (var product in new[] { "alpha.product", "beta.product" })
        {
            var channel = await reader.ReadChannelAsync(product, "live", trusted);
            var verifiedRelease = await reader.ReadReleaseAsync(product, channel.Pointer.ReleaseId, trusted, channel.Pointer.ReleaseDigest);
            Assert.Equal(product, verifiedRelease.Lock.ProductId);
            Assert.Equal(channel.Pointer.ReleaseDigest, ContentHash.Compute(verifiedRelease.EnvelopeBytes));
            Assert.NotNull(await reader.ReadRevocationsAsync(product, trusted, now: fixtureNow));
            Assert.NotNull(await store.OpenAsync(layout.ReleaseBundle(product, channel.Pointer.ReleaseId)));
            Assert.NotNull(await store.OpenAsync(layout.Coverage(product, channel.Pointer.ReleaseId)));
        }
        var manifests = new Dictionary<PackageId, PackageManifest>();
        foreach (var id in new[] { "alpha.core", "alpha.ui.classic", "alpha.ui.modern", "beta.core", "beta.tools" })
        {
            var package = new PackageId(id);
            var manifest = await repository.GetManifestAsync(package, new PackageVersion("1.0.0", 1));
            Assert.NotNull(manifest);
            manifests[package] = manifest!;
            var rows = new List<PackageFileEntry>();
            await foreach (var row in repository.ReadFileTableAsync(manifest!)) rows.Add(row);
            Assert.Single(rows);
            await using var blob = await store.OpenAsync(layout.Blob(rows[0].Content));
            Assert.NotNull(blob);
        }

        var release = CreateGoldenRelease("alpha.product", "alpha-1", manifests.Values.Where(x => x.Id.Value.StartsWith("alpha.", StringComparison.Ordinal)).ToArray(), BuildGoldenAxes());
        foreach (var ui in new[] { "classic", "modern" })
        {
            var selection = GoldenSelection(ui);
            var resolution = new VariantResolver().Resolve(release, selection);
            Assert.True(resolution.IsValid, string.Join('\n', resolution.Diagnostics));
            var composed = await new FileSetComposer().ComposeAsync(repository, resolution);
            Assert.True(composed.IsValid, string.Join('\n', composed.Diagnostics));
            Assert.NotEmpty(composed.Files);
            Assert.Contains(composed.Files.Values, x => x.Owner.Value == $"alpha.ui.{ui}");
            Assert.Equal(composed.FileSetId, FileSetIdentity.Compute(composed.Files));
        }
    }

    [Fact]
    public async Task CheckedInGoldenTreeRejectsUnknownRepositorySchemaVersion()
    {
        await using var store = await LoadGoldenStoreAsync();
        var bytes = await ReadObjectAsync(store, new ObjectKey("repo.json"));
        var mutated = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal));
        await store.PutAsync(new ObjectKey("repo.json"), new MemoryStream(mutated), mutated.LongLength);

        var error = await Assert.ThrowsAsync<FormatException>(() => RepositoryFactory.LoadDescriptorAsync(store).AsTask());
        Assert.Contains("schemaVersion 2", error.Message, StringComparison.Ordinal);
        Assert.Contains("minimumClientVersion", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaticProjectionsAndReleaseBundleAreByteStableAndPreserveLockEnvelope()
    {
        await using var store = await LoadGoldenStoreAsync();
        var descriptor = new RepositoryDescriptor { RepositoryId = "golden-v1", GeneratedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z") };
        var layout = new RepositoryLayout(descriptor.Layout);
        var package = await new StaticRepository(store, descriptor).GetManifestAsync(new PackageId("alpha.core"), new PackageVersion("1.0.0", 1));
        Assert.NotNull(package);
        var writer = new StaticProjectionWriter(store, layout);
        var checkedInIndex = await ReadObjectAsync(store, layout.PackageIndex(package!.Id));
        var checkedInCanonicalIndex = await ReadObjectAsync(store, new ObjectKey($"packages/{package.Id.Value}/index.json"));
        await writer.WritePackageIndexAsync(package!.Id, [package]);
        Assert.Equal(checkedInIndex, await ReadObjectAsync(store, layout.PackageIndex(package.Id)));
        Assert.Equal(checkedInCanonicalIndex, await ReadObjectAsync(store, new ObjectKey($"packages/{package.Id.Value}/index.json")));
        await writer.WritePackageIndexAsync(package.Id, [package]);
        Assert.Equal(checkedInIndex, await ReadObjectAsync(store, layout.PackageIndex(package.Id)));
        Assert.Equal(checkedInCanonicalIndex, await ReadObjectAsync(store, new ObjectKey($"packages/{package.Id.Value}/index.json")));

        var repository = new StaticRepository(store, descriptor);
        var classic = await repository.GetManifestAsync(new PackageId("alpha.ui.classic"), new PackageVersion("1.0.0", 1));
        Assert.NotNull(classic);
        var verified = await new RepositoryReader(store, descriptor).ReadReleaseAsync("alpha.product", "alpha-1", new Dictionary<string, byte[]>
        {
            ["fixture-root"] = Base64Url.Decode("ebVWLo_mVPlAeLES6KmLp5AfhTrmlb7X4OORC60ElmQ")
        });
        var envelope = verified.EnvelopeBytes;
        var checkedInBundle = await ReadObjectAsync(store, layout.ReleaseBundle("alpha.product", "alpha-1"));
        await writer.WriteReleaseBundleAsync("alpha.product", "alpha-1", envelope, [package, classic!]);
        Assert.Equal(checkedInBundle, await ReadObjectAsync(store, layout.ReleaseBundle("alpha.product", "alpha-1")));
        var bundle = JsonDocument.Parse(await ReadObjectAsync(store, layout.ReleaseBundle("alpha.product", "alpha-1")));
        Assert.Equal(Base64Url.Encode(envelope), bundle.RootElement.GetProperty("lockEnvelope").GetString());
        await new RepositoryReader(store, descriptor).ReadReleaseAsync("alpha.product", "alpha-1", new Dictionary<string, byte[]>
        {
            ["fixture-root"] = Base64Url.Decode("ebVWLo_mVPlAeLES6KmLp5AfhTrmlb7X4OORC60ElmQ")
        });
    }

    [Fact]
    public void SchemaFixturesAreValidatedByTheirCheckedInSchemas()
    {
        var schemaDirectory = Path.Combine(AppContext.BaseDirectory, "schemas");
        var schemas = Directory.EnumerateFiles(schemaDirectory, "*.schema.json")
            .Select(path => (Path: path, Schema: JsonSchema.FromFile(path)))
            .ToArray();
        foreach (var (_, schema) in schemas) SchemaRegistry.Global.Register(schema);
        foreach (var (schemaPath, schema) in schemas)
        {
            var schemaName = Path.GetFileNameWithoutExtension(schemaPath).Replace(".schema", string.Empty, StringComparison.Ordinal);
            var fixtureDirectory = Path.Combine(schemaDirectory, "fixtures", schemaName);
            Assert.True(Directory.Exists(fixtureDirectory), $"Schema '{schemaName}' is missing its checked-in fixture directory.");
            var fixturePaths = Directory.EnumerateFiles(fixtureDirectory, "*.json").ToArray();
            Assert.Contains(fixturePaths, path => Path.GetFileName(path).Equals("valid-basic.json", StringComparison.Ordinal));
            Assert.Contains(fixturePaths, path => Path.GetFileName(path).Equals("invalid-unknown-field.json", StringComparison.Ordinal));
            foreach (var fixturePath in fixturePaths)
            {
                using var instance = JsonDocument.Parse(File.ReadAllBytes(fixturePath));
                var valid = schema.Evaluate(instance.RootElement).IsValid;
                var expected = Path.GetFileName(fixturePath).StartsWith("valid-", StringComparison.Ordinal);
                Assert.Equal(expected, valid);
            }
        }
    }

    [Fact]
    public void CheckedInGoldenDocumentsValidateAgainstTheirSchemas()
    {
        var schemaDirectory = Path.Combine(AppContext.BaseDirectory, "schemas");
        var schemas = Directory.EnumerateFiles(schemaDirectory, "*.schema.json")
            .ToDictionary(path => Path.GetFileNameWithoutExtension(path).Replace(".schema", string.Empty, StringComparison.Ordinal), JsonSchema.FromFile, StringComparer.Ordinal);
        foreach (var schema in schemas.Values) SchemaRegistry.Global.Register(schema);
        var goldenRoot = Path.Combine(AppContext.BaseDirectory, "vectors", "golden");
        foreach (var path in Directory.EnumerateFiles(goldenRoot, "*.json", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(goldenRoot, path).Replace(Path.DirectorySeparatorChar, '/');
            var schemaName = relative switch
            {
                "repo.json" => "repo",
                "keys.json" => "signed-envelope",
                _ when relative.EndsWith("/package.json", StringComparison.Ordinal) => "package",
                _ when relative.EndsWith("/release.lock.json", StringComparison.Ordinal) => "signed-envelope",
                _ when relative.EndsWith("/release.bundle.json", StringComparison.Ordinal) => "release-bundle",
                _ when relative.EndsWith("/coverage.json", StringComparison.Ordinal) => "coverage",
                _ when relative.EndsWith("/channels/live.json", StringComparison.Ordinal) => "signed-envelope",
                _ when relative.EndsWith("/revocations.json", StringComparison.Ordinal) => "signed-envelope",
                _ => null
            };
            if (schemaName is null) continue;
            using var instance = JsonDocument.Parse(File.ReadAllBytes(path));
            Assert.True(schemas[schemaName].Evaluate(instance.RootElement).IsValid, $"{relative} does not validate against {schemaName}.schema.json");
        }
    }

    [Fact]
    public async Task SignedKeyManifestRotationRejectsChangedSameSequenceBytes()
    {
        await using var store = new MemoryObjectStore();
        var layout = new RepositoryLayout(new RepositoryLayoutTemplates());
        var descriptor = new RepositoryDescriptor { RepositoryId = "repo", GeneratedAt = DateTimeOffset.UtcNow };
        var reader = new RepositoryReader(store, descriptor);
        var root = FixtureKey(110);
        var next = FixtureKey(142);
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var rootRecord = new PublicKeyRecord { KeyId = "root", Algorithm = "ed25519", PublicKey = Base64Url.Encode(root.PublicKey), NotBefore = now.AddDays(-1), NotAfter = now.AddDays(2) };
        var nextRecord = new PublicKeyRecord { KeyId = "next", Algorithm = "ed25519", PublicKey = Base64Url.Encode(next.PublicKey), NotBefore = now.AddHours(-1), NotAfter = now.AddDays(2) };
        var first = new KeyManifest { KeySequence = 1, Keys = [rootRecord] };
        var firstBytes = SignedDocument.SerializeEnvelope(SignedDocument.Sign("key-manifest", SignedDocument.SerializePayload(first), "root", root.PrivateKey));
        await store.PutAsync(layout.KeyManifest(), new MemoryStream(firstBytes), firstBytes.LongLength);
        var verifiedFirst = await reader.ReadKeyManifestAsync(new Dictionary<string, byte[]> { ["root"] = root.PublicKey }, now: now);

        var rotated = first with { KeySequence = 2, Keys = [rootRecord, nextRecord] };
        var rotatedBytes = SignedDocument.SerializeEnvelope(SignedDocument.Sign("key-manifest", SignedDocument.SerializePayload(rotated), "root", root.PrivateKey));
        await store.PutAsync(layout.KeyManifest(), new MemoryStream(rotatedBytes), rotatedBytes.LongLength);
        var verifiedRotated = await reader.ReadKeyManifestAsync(new Dictionary<string, byte[]> { ["root"] = root.PublicKey }, verifiedFirst.Manifest.KeySequence, ContentHash.Compute(firstBytes), now: now);
        Assert.Equal(2, verifiedRotated.Manifest.KeySequence);

        var changed = rotated with { Keys = [rootRecord, nextRecord, new PublicKeyRecord { KeyId = "third", Algorithm = "ed25519", PublicKey = Base64Url.Encode(FixtureKey(174).PublicKey), NotBefore = now.AddHours(-1), NotAfter = now.AddDays(2) }] };
        var changedBytes = SignedDocument.SerializeEnvelope(SignedDocument.Sign("key-manifest", SignedDocument.SerializePayload(changed), "root", root.PrivateKey));
        await store.PutAsync(layout.KeyManifest(), new MemoryStream(changedBytes), changedBytes.LongLength);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadKeyManifestAsync(new Dictionary<string, byte[]> { ["root"] = root.PublicKey }, 2, ContentHash.Compute(rotatedBytes), now: now).AsTask());
    }

    [Fact]
    public async Task SignedRevocationReaderAcceptsFreshEmptyAndRejectsStaleDocuments()
    {
        await using var store = new MemoryObjectStore();
        var layout = new RepositoryLayout(new RepositoryLayoutTemplates());
        var descriptor = new RepositoryDescriptor { RepositoryId = "repo", GeneratedAt = DateTimeOffset.UtcNow };
        var reader = new RepositoryReader(store, descriptor);
        var key = FixtureKey(206);
        var trusted = new Dictionary<string, byte[]> { ["root"] = key.PublicKey };
        var now = DateTimeOffset.UtcNow;
        var fresh = new RevocationDocument { ProductId = "product", RevocationSequence = 1, UpdatedAt = now, Entries = [] };
        var freshBytes = SignedDocument.SerializeEnvelope(SignedDocument.Sign("revocation", SignedDocument.SerializePayload(fresh), "root", key.PrivateKey));
        await store.PutAsync(layout.Revocations("product"), new MemoryStream(freshBytes), freshBytes.LongLength);
        var verified = await reader.ReadRevocationsAsync("product", trusted, now: now);
        Assert.NotNull(verified);
        Assert.Empty(verified!.Document.Entries);

        var stale = fresh with { RevocationSequence = 2, UpdatedAt = now.AddDays(-8) };
        var staleBytes = SignedDocument.SerializeEnvelope(SignedDocument.Sign("revocation", SignedDocument.SerializePayload(stale), "root", key.PrivateKey));
        await store.PutAsync(layout.Revocations("product"), new MemoryStream(staleBytes), staleBytes.LongLength);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadRevocationsAsync("product", trusted, 1, ContentHash.Compute(freshBytes), now: now).AsTask());
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static Ed25519KeyPair FixtureKey(byte seed)
    {
        var privateKey = Enumerable.Range(0, 32).Select(offset => (byte)(seed + offset)).ToArray();
        var parameters = new Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(privateKey, 0);
        return new Ed25519KeyPair(privateKey, parameters.GeneratePublicKey().GetEncoded());
    }

    private static string VectorPath(string name) => Path.Combine(AppContext.BaseDirectory, "vectors", "signed", name);
    private static string GoldenPath(string name) => Path.Combine(AppContext.BaseDirectory, "vectors", "golden", name);

    private static async ValueTask<MemoryObjectStore> LoadGoldenStoreAsync()
    {
        var store = new MemoryObjectStore();
        var root = Path.Combine(AppContext.BaseDirectory, "vectors", "golden");
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Equals("README.md", StringComparison.Ordinal)) continue;
            await using var content = File.OpenRead(path);
            await store.PutAsync(new ObjectKey(relative), content);
        }
        return store;
    }

    private static async ValueTask<byte[]> ReadObjectAsync(IReadableObjectStore store, ObjectKey key)
    {
        var result = await store.OpenAsync(key);
        Assert.NotNull(result);
        await using (result!)
        using (var bytes = new MemoryStream())
        {
            await result.Content.CopyToAsync(bytes);
            return bytes.ToArray();
        }
    }

    private static ImmutableArray<AxisDefinition> BuildGoldenAxes() =>
    [
        new AxisDefinition { Name = "ui", Rank = 10, Cardinality = AxisCardinality.One, Required = true, Default = "modern", Values = [new AxisValue { Id = "classic" }, new AxisValue { Id = "modern" }] },
        new AxisDefinition { Name = "platform", Rank = 20, Cardinality = AxisCardinality.One, Required = true, Default = "linux", Values = [new AxisValue { Id = "linux" }, new AxisValue { Id = "windows" }] },
        new AxisDefinition { Name = "language", Rank = 30, Cardinality = AxisCardinality.Many, Required = false, Values = [new AxisValue { Id = "de" }, new AxisValue { Id = "en" }] },
        new AxisDefinition { Name = "edition", Rank = 40, Cardinality = AxisCardinality.One, Required = false, Default = "standard", Values = [new AxisValue { Id = "pro" }, new AxisValue { Id = "standard" }] },
        new AxisDefinition { Name = "region", Rank = 50, Cardinality = AxisCardinality.Many, Required = false, Values = [new AxisValue { Id = "eu" }, new AxisValue { Id = "us" }] }
    ];

    private static VariantSelection GoldenSelection(string ui) => new()
    {
        Axes = new Dictionary<string, ImmutableSortedSet<string>>(StringComparer.Ordinal)
        {
            ["ui"] = ImmutableSortedSet.Create(StringComparer.Ordinal, ui),
            ["platform"] = ImmutableSortedSet.Create(StringComparer.Ordinal, "linux"),
            ["language"] = ImmutableSortedSet.Create(StringComparer.Ordinal, "de", "en"),
            ["edition"] = ImmutableSortedSet.Create(StringComparer.Ordinal, "standard"),
            ["region"] = ImmutableSortedSet.Create(StringComparer.Ordinal, "eu")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };

    private static ReleaseLock CreateGoldenRelease(string product, string releaseId, IEnumerable<PackageManifest> manifests, ImmutableArray<AxisDefinition> axes)
    {
        var packageArray = manifests.ToImmutableArray();
        var requirements = packageArray.Select(manifest => new PackageRequirement
        {
            Package = manifest.Id,
            When = manifest.Id.Value switch
            {
                "alpha.ui.classic" => Predicate("ui", "classic"),
                "alpha.ui.modern" => Predicate("ui", "modern"),
                _ => AxisPredicate.Always
            }
        }).ToImmutableArray();
        return new ReleaseLock
        {
            SchemaVersion = 1, ProductId = product, ReleaseId = releaseId, Sequence = 1, State = ReleaseState.Published,
            CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"), Axes = axes, Requirements = requirements,
            Packages = packageArray.Select(Pin).ToImmutableArray(), CoverageDigest = ContentHash.Compute("golden-coverage"u8)
        };
    }

    private static AxisPredicate Predicate(string axis, params string[] values) => new()
    {
        Constraints = new Dictionary<string, ImmutableSortedSet<string>>(StringComparer.Ordinal) { [axis] = values.ToImmutableSortedSet(StringComparer.Ordinal) }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };

    private static LockedPackage Pin(PackageManifest manifest) => new()
    {
        Id = manifest.Id, Version = manifest.Version, Sequence = manifest.Sequence, ManifestPath = $"packages/{manifest.Id.Value}/{manifest.Version.Label}/package.json",
        // The lock binds the exact bytes on disk, not a re-serialization of the parsed model.
        // This is intentionally an interop assertion for checked-in golden artifacts.
        ManifestDigest = ContentHash.Compute(File.ReadAllBytes(GoldenPath($"packages/{manifest.Id.Value}/{manifest.Version.Label}/package.json"))),
        FileCount = manifest.FileCount, InstallSize = manifest.InstallSize, DownloadSize = manifest.DownloadSize
    };
}
