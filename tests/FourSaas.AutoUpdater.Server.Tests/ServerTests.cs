using FourSaas.AutoUpdater.Core;
using FourSaas.AutoUpdater.Server;
using FourSaas.AutoUpdater.Storage;
using FourSaas.AutoUpdater.Storage.Memory;
using FourSaas.AutoUpdater.Publishing;
using FourSaas.AutoUpdater.Repository;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.Json.Nodes;

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
        await state.AllocateSequenceAsync("default", "release", "demo");
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
        Assert.True(state.Releases.TryGetValue(("demo", "r1"), out var apiBytes));
        Assert.Equal(envelopeBytes, apiBytes);
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

    [Fact]
    public async Task StagingKeysAreRepositoryScopedAndSealRejectsWrongRepository()
    {
        await using var store = new MemoryObjectStore();
        var state = new ManagementState(store);
        var session = state.OpenSession("repo-a");
        var sessionId = (string)session.GetType().GetProperty("sessionId")!.GetValue(session)!;
        var digest = ContentHash.Compute("abc"u8).ToString();
        var requestContext = new DefaultHttpContext();
        requestContext.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
        {
            items = new[] { new { sha256 = digest, storedLength = 3L } }
        }));

        var grants = await state.CreateGrantsAsync(sessionId, requestContext.Request, "repo-a");
        var responseContext = new DefaultHttpContext();
        responseContext.Response.Body = new MemoryStream();
        responseContext.RequestServices = new ServiceCollection()
            .AddLogging()
            .Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(_ => { })
            .BuildServiceProvider();
        await grants.ExecuteAsync(responseContext);
        responseContext.Response.Body.Position = 0;
        using var response = await JsonDocument.ParseAsync(responseContext.Response.Body);
        var stagingKey = response.RootElement.GetProperty("items")[0].GetProperty("stagingKey").GetString();
        Assert.StartsWith("_staging/repo-a/", stagingKey, StringComparison.Ordinal);

        var wrongRepositorySeal = await state.SealSessionAsync("repo-b", sessionId);
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(wrongRepositorySeal);
        Assert.Equal(StatusCodes.Status404NotFound, status.StatusCode);
    }

    [Fact]
    public async Task StagingGcDoesNotDeleteAnotherRepository()
    {
        await using var store = new MemoryObjectStore();
        var state = new ManagementState(store);
        var foreignKey = new ObjectKey("_staging/repo-b/stale/grant");
        await store.PutAsync(foreignKey, new MemoryStream("foreign"u8.ToArray()), 7);

        var result = await state.CollectStagingGcAsync("repo-a", dryRun: false);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status200OK, status.StatusCode);
        Assert.NotNull(await store.HeadAsync(foreignKey));
    }

    [Fact]
    public void ChannelSequenceUniqueIndexIncludesChannel()
    {
        var options = new DbContextOptionsBuilder<ManagementDbContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .Options;
        using var database = new ManagementDbContext(options);
        var entity = database.Model.FindEntityType(typeof(ChannelRow));
        Assert.NotNull(entity);
        Assert.Contains(entity!.GetIndexes(), index => index.IsUnique && index.Properties.Select(x => x.Name).SequenceEqual([
            nameof(ChannelRow.RepositoryId),
            nameof(ChannelRow.ProductId),
            nameof(ChannelRow.Channel),
            nameof(ChannelRow.ChannelSequence)]));
    }

    [Fact]
    public async Task EmptyRevocationIsAcceptedAtRoutePlacement()
    {
        await using var store = new MemoryObjectStore();
        var state = new ManagementState(store);
        var keys = Ed25519KeyPair.Create();
        state.ConfigureTrustedKeys([("root", keys.PublicKey)]);
        await state.AllocateSequenceAsync("default", "revocation", "demo");
        var document = new RevocationDocument
        {
            ProductId = "demo",
            RevocationSequence = 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            Entries = []
        };
        var payload = SignedDocument.SerializePayload(document);
        var envelope = SignedDocument.SerializeEnvelope(SignedDocument.Sign("revocation", payload, "root", keys.PrivateKey));
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(envelope);

        var result = await state.PlaceSignedAsync("revocation", "demo", "r1", context.Request, state.Revocations);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NoContent>(result);
    }

    [Fact]
    public async Task SealRejectsExpiredGrant()
    {
        var persistencePath = Path.Combine(Path.GetTempPath(), "4sup-expired-grant-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await using var store = new MemoryObjectStore();
            var state = new ManagementState(store, persistencePath);
            var session = state.OpenSession("repo");
            var sessionId = (string)session.GetType().GetProperty("sessionId")!.GetValue(session)!;
            var request = new DefaultHttpContext();
            request.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
            {
                items = new[] { new { sha256 = ContentHash.Compute("abc"u8).ToString(), storedLength = 3L } }
            }));

            using var grantsResponse = await ExecuteJsonAsync(await state.CreateGrantsAsync(sessionId, request.Request, "repo"));
            var grantId = grantsResponse.RootElement.GetProperty("items")[0].GetProperty("grantId").GetString()!;
            var persisted = JsonNode.Parse(await File.ReadAllTextAsync(persistencePath))!.AsObject();
            var grant = persisted["grants"]!.AsObject()[grantId]!.AsObject();
            grant["expiresAt"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
            await File.WriteAllTextAsync(persistencePath, persisted.ToJsonString());

            var reloaded = new ManagementState(store, persistencePath);
            var result = await reloaded.SealSessionAsync("repo", sessionId);

            var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
            Assert.Equal(StatusCodes.Status400BadRequest, status.StatusCode);
        }
        finally
        {
            if (File.Exists(persistencePath)) File.Delete(persistencePath);
        }
    }

    [Fact]
    public async Task SealQuarantinesStagedObjectWhenDigestVerificationFails()
    {
        await using var store = new MemoryObjectStore();
        var state = new ManagementState(store);
        var session = state.OpenSession("repo");
        var sessionId = (string)session.GetType().GetProperty("sessionId")!.GetValue(session)!;
        var request = new DefaultHttpContext();
        request.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
        {
            items = new[] { new { sha256 = ContentHash.Compute("abc"u8).ToString(), storedLength = 3L } }
        }));

        using var grantsResponse = await ExecuteJsonAsync(await state.CreateGrantsAsync(sessionId, request.Request, "repo"));
        var stagingKey = new ObjectKey(grantsResponse.RootElement.GetProperty("items")[0].GetProperty("stagingKey").GetString()!);
        await store.PutAsync(stagingKey, new MemoryStream("bad"u8.ToArray()), 3);

        var result = await state.SealSessionAsync("repo", sessionId);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, status.StatusCode);
        Assert.Null(await store.HeadAsync(stagingKey));
        var quarantined = new List<ObjectKey>();
        await foreach (var key in store.ListAsync("_trash/staging/")) quarantined.Add(key);
        Assert.Single(quarantined);
    }

    [Fact]
    public async Task CheckReleaseDraftUsesThePinnedPublishedPackageVersion()
    {
        var package = new PackageId("demo.pkg");
        var oldManifest = CreateManifest(package, "1.0.0", 1);
        var pinnedManifest = CreateManifest(package, "2.0.0", 2);
        var state = new ManagementState();
        await state.AllocateSequenceAsync("default", "release", "demo");
        state.PackageVersions[("default", package.Value, pinnedManifest.Version.Label)] = RepositoryJson.SerializeManifest(pinnedManifest);
        state.PackageVersions[("default", package.Value, oldManifest.Version.Label)] = RepositoryJson.SerializeManifest(oldManifest);
        state.PublishedPackageVersions[("default", package.Value, pinnedManifest.Version.Label)] = true;
        state.ReleaseDraftProducts["draft"] = "demo";
        state.ReleaseDrafts["draft"] = SignedDocument.SerializePayload(new ReleaseLock
        {
            SchemaVersion = 1,
            ProductId = "demo",
            ReleaseId = "r1",
            Sequence = 1,
            State = ReleaseState.Draft,
            CreatedAt = DateTimeOffset.UtcNow,
            Axes = [],
            Requirements = [new PackageRequirement { Package = package }],
            Packages = [CreatePin(pinnedManifest)],
            CoverageDigest = ContentHash.Compute([])
        });

        var result = state.CheckReleaseDraft("demo", "draft");

        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;
        Assert.True((bool)value.GetType().GetProperty("valid")!.GetValue(value)!);
    }

    private static async Task<JsonDocument> ExecuteJsonAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(_ => { })
            .BuildServiceProvider();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(context.Response.Body);
    }

    private static PackageManifest CreateManifest(PackageId package, string version, long sequence) => new()
    {
        SchemaVersion = 1,
        Id = package,
        Version = new PackageVersion(version, sequence),
        Sequence = sequence,
        Kind = PackageKind.Content,
        CreatedAt = DateTimeOffset.UtcNow,
        PathPrefixes = [],
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

    private static LockedPackage CreatePin(PackageManifest manifest) => new()
    {
        Id = manifest.Id,
        Version = manifest.Version,
        Sequence = manifest.Sequence,
        ManifestPath = $"packages/{manifest.Id.Value}/{manifest.Version.Label}/package.json",
        ManifestDigest = ContentHash.Compute(RepositoryJson.SerializeManifest(manifest)),
        FileCount = manifest.FileCount,
        InstallSize = manifest.InstallSize,
        DownloadSize = manifest.DownloadSize
    };
}
