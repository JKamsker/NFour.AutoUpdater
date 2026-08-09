using FourSaas.AutoUpdater.Core;
using FourSaas.AutoUpdater.Repository;
using FourSaas.AutoUpdater.Server;
using FourSaas.AutoUpdater.Storage;
using FourSaas.AutoUpdater.Storage.Memory;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json;

namespace FourSaas.AutoUpdater.Server.Tests;

[Collection(PostgresIntegrationCollection.Name)]
public sealed class MultiInstancePostgresTests
{
    private const string RepositoryPrefix = "multi";
    private const string CompactGuidFormat = "N";
    private const int RepositorySuffixLength = 12;
    private const int ConcurrentAllocationCount = 20;
    private const int AllocationStartIndex = 0;
    private const long FirstSequence = 1;
    private const string SequenceScope = "release";
    private const string SequenceName = "product";
    private const string FirstPayloadText = "first instance payload";
    private const string SecondPayloadText = "second instance payload";
    private const string ItemsProperty = "items";
    private const string StagingKeyProperty = "stagingKey";
    private const string SessionIdProperty = "sessionId";
    private const string SequenceProperty = "sequence";

    [Fact]
    public async Task TwoInstancesAllocateGrantSealAndCollectConcurrently()
    {
        var connection = PostgresIntegrationSettings.GetRequiredConnectionString();

        var options = new DbContextOptionsBuilder<ManagementDbContext>().UseNpgsql(connection).Options;
        await using (var database = new ManagementDbContext(options)) await database.Database.MigrateAsync();
        var factory = new TestDbContextFactory(options);
        await using var repositoryStore = new MemoryObjectStore();
        await using var stagingStore = new MemoryObjectStore();
        var repository = RepositoryPrefix + Guid.NewGuid().ToString(CompactGuidFormat)[..RepositorySuffixLength];
        var first = new ManagementState(repositoryStore, stagingStore: stagingStore, databaseFactory: factory, repositoryId: repository);
        var second = new ManagementState(repositoryStore, stagingStore: stagingStore, databaseFactory: factory, repositoryId: repository);
        var instances = new[] { first, second };

        var allocations = await Task.WhenAll(Enumerable.Range(AllocationStartIndex, ConcurrentAllocationCount).Select(index =>
            instances[index % instances.Length].AllocateSequenceAsync(repository, SequenceScope, SequenceName)));
        Assert.Equal(Enumerable.Range((int)FirstSequence, ConcurrentAllocationCount).Select(value => (long)value), allocations.Select(SequenceOf).Order());

        var firstSession = SessionIdOf(first.OpenSession(repository));
        var secondSession = SessionIdOf(second.OpenSession(repository));
        var firstPayload = Encoding.UTF8.GetBytes(FirstPayloadText);
        var secondPayload = Encoding.UTF8.GetBytes(SecondPayloadText);
        var grants = await Task.WhenAll(
            MintGrantAsync(first, repository, firstSession, firstPayload),
            MintGrantAsync(second, repository, secondSession, secondPayload));
        await Task.WhenAll(
            stagingStore.PutAsync(grants[0].StagingKey, new MemoryStream(firstPayload), firstPayload.LongLength).AsTask(),
            stagingStore.PutAsync(grants[1].StagingKey, new MemoryStream(secondPayload), secondPayload.LongLength).AsTask());

        var concurrent = await Task.WhenAll(
            first.SealSessionAsync(repository, firstSession),
            second.SealSessionAsync(repository, secondSession),
            first.CollectStagingGcAsync(repository, dryRun: false),
            second.CollectStagingGcAsync(repository, dryRun: false));

        Assert.All(concurrent, result => Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode));
        Assert.NotNull(await repositoryStore.HeadAsync(new RepositoryLayout(new RepositoryLayoutTemplates()).Blob(grants[0].Digest)));
        Assert.NotNull(await repositoryStore.HeadAsync(new RepositoryLayout(new RepositoryLayoutTemplates()).Blob(grants[1].Digest)));
    }

    private static async Task<(ObjectKey StagingKey, ContentHash Digest)> MintGrantAsync(
        ManagementState state,
        string repository,
        string sessionId,
        byte[] payload)
    {
        var digest = ContentHash.Compute(payload);
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
        {
            items = new[] { new { sha256 = digest.ToString(), storedLength = payload.LongLength } }
        }));
        using var response = await ExecuteJsonAsync(await state.CreateGrantsAsync(sessionId, context.Request, repository));
        var stagingKey = new ObjectKey(response.RootElement.GetProperty(ItemsProperty)[0].GetProperty(StagingKeyProperty).GetString()!);
        return (stagingKey, digest);
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

    private static string SessionIdOf(object session)
        => (string)session.GetType().GetProperty(SessionIdProperty)!.GetValue(session)!;

    private static long SequenceOf(IResult result)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;
        return (long)value.GetType().GetProperty(SequenceProperty)!.GetValue(value)!;
    }

    private sealed class TestDbContextFactory(DbContextOptions<ManagementDbContext> options) : IDbContextFactory<ManagementDbContext>
    {
        public ManagementDbContext CreateDbContext() => new(options);
    }
}
