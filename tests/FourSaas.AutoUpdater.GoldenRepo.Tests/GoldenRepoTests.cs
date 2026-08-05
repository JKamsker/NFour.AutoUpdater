using FourSaas.AutoUpdater.Core;
using FourSaas.AutoUpdater.Repository;
using FourSaas.AutoUpdater.Storage;
using FourSaas.AutoUpdater.Storage.Memory;
using System.Text;
using System.Text.Json;
using Xunit;

namespace FourSaas.AutoUpdater.GoldenRepo.Tests;

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
    public void LayoutRejectsCaseAmbiguousIdentifiers()
    {
        Assert.Throws<ArgumentException>(() => new PackageId("UPPER"));
    }

    [Fact]
    public void SignedVectorsVerifyAndRejectDuplicatePayloadProperties()
    {
        var key = Base64Url.Decode("A6EHv_POEL4dcN0Y50vAmWfk1jCbpQ1fHdyGZBJVMbg");
        var trusted = new Dictionary<string, byte[]> { ["vector-key"] = key };
        var validBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "vectors", "signed", "valid-envelope.json"));
        var valid = SignedDocument.DeserializeEnvelope(validBytes);
        Assert.True(SignedDocument.Verify(valid, trusted, out var validPayload, out var validError), validError);
        Assert.Equal("{\"message\": \"héllo\",\n \"number\": -7}", Encoding.UTF8.GetString(Base64Url.Decode(valid.Payload)));
        JsonRules.Validate(validPayload);

        var duplicateBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "vectors", "signed", "duplicate-envelope.json"));
        var duplicate = SignedDocument.DeserializeEnvelope(duplicateBytes);
        Assert.False(SignedDocument.Verify(duplicate, trusted, out _, out var duplicateError));
        Assert.Contains("Duplicate JSON", duplicateError, StringComparison.Ordinal);
    }

    [Fact]
    public void SignedPolicyMetadataVectorsAreExecutableInputs()
    {
        foreach (var name in new[] { "key-rotation.json", "rollback-chain.json", "revocation-empty-trust.json" })
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "vectors", "signed", name)));
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            Assert.True(document.RootElement.TryGetProperty("expected", out _));
            Assert.True(document.RootElement.TryGetProperty("type", out _));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
