using FourSaas.AutoUpdater.Core;
using FourSaas.AutoUpdater.Server;
using FourSaas.AutoUpdater.Storage;
using FourSaas.AutoUpdater.Storage.Memory;
using FourSaas.AutoUpdater.Publishing;
using Microsoft.AspNetCore.Http;

namespace FourSaas.AutoUpdater.Server.Tests;

public sealed class ServerTests
{
    [Fact]
    public async Task VerifiedPlacementWritesOpaqueBytesToStaticProjection()
    {
        await using var store = new MemoryObjectStore();
        var state = new ManagementState(store);
        var keys = Ed25519KeyPair.Create();
        state.ConfigureTrustedKeys([("root", keys.PublicKey)]);
        var coverageWithoutDigest = new CoverageDocument { ReleaseId = "r1", Mode = "exhaustive", PointCount = 0, Points = [] };
        var coverageDigest = ContentHash.Compute(CoverageGenerator.SerializeForDigest(coverageWithoutDigest));
        var coverageBytes = CoverageGenerator.Serialize(coverageWithoutDigest with { Digest = coverageDigest });
        await store.PutAsync(new RepositoryLayout(new RepositoryLayoutTemplates()).Coverage("demo", "r1"), new MemoryStream(coverageBytes), coverageBytes.LongLength);
        var release = new ReleaseLock { SchemaVersion = 1, ProductId = "demo", ReleaseId = "r1", Sequence = 1, State = ReleaseState.Published, CreatedAt = DateTimeOffset.UtcNow, Axes = [], Requirements = [], Packages = [], CoverageDigest = coverageDigest };
        var payload = SignedDocument.SerializePayload(release);
        var envelopeBytes = SignedDocument.SerializeEnvelope(SignedDocument.Sign("release-lock", payload, "root", keys.PrivateKey));
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(envelopeBytes);
        var result = await state.PlaceSignedAsync("release-lock", "demo", "r1", context.Request, state.Releases);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NoContent>(result);
        var layout = new RepositoryLayout(new RepositoryLayoutTemplates());
        var projection = await store.OpenAsync(layout.Release("demo", "r1"));
        Assert.NotNull(projection);
        await using (projection!)
        {
            using var bytes = new MemoryStream();
            await projection.Content.CopyToAsync(bytes);
            Assert.Equal(envelopeBytes, bytes.ToArray());
        }
    }

    [Fact]
    public void PublishSessionStateSurvivesRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), "4sup-state-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            string sessionId;
            var first = new ManagementState(null, path);
            var result = first.OpenSession("repo");
            sessionId = (string)result.GetType().GetProperty("sessionId")!.GetValue(result)!;
            var second = new ManagementState(null, path);
            Assert.Contains(sessionId, second.Sessions.Keys);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
