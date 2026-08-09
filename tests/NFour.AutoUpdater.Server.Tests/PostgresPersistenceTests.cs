using NFour.AutoUpdater.Server;
using NFour.AutoUpdater.Storage.Memory;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace NFour.AutoUpdater.Server.Tests;

[Collection(PostgresIntegrationCollection.Name)]
public sealed class PostgresPersistenceTests
{
    private const string IntegrationRepositoryPrefix = "ci";
    private const int IntegrationRepositorySuffixLength = 12;
    private const string ReleaseSequenceScope = "release";
    private const string DemoSequenceName = "demo";
    private const string SequencePropertyName = "sequence";
    private const string CompactGuidFormat = "N";
    private const long InitialSequence = 1;
    private const long RestartedSequence = 2;

    [Fact]
    public async Task MigrationAndSequenceReservationSurviveStateRestart()
    {
        var connection = PostgresIntegrationSettings.GetRequiredConnectionString();
        var options = new DbContextOptionsBuilder<ManagementDbContext>().UseNpgsql(connection).Options;
        await using (var database = new ManagementDbContext(options))
            await database.Database.MigrateAsync();
        var factory = new TestDbContextFactory(options);
        await using var store = new MemoryObjectStore();
        var repository = IntegrationRepositoryPrefix
            + Guid.NewGuid().ToString(CompactGuidFormat)[..IntegrationRepositorySuffixLength];

        var first = new ManagementState(store, databaseFactory: factory, repositoryId: repository);
        var firstResult = await first.AllocateSequenceAsync(repository, ReleaseSequenceScope, DemoSequenceName);
        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(firstResult).StatusCode);
        Assert.Equal(InitialSequence, SequenceOf(firstResult));

        var second = new ManagementState(store, databaseFactory: factory, repositoryId: repository);
        var secondResult = await second.AllocateSequenceAsync(repository, ReleaseSequenceScope, DemoSequenceName);
        Assert.Equal(RestartedSequence, SequenceOf(secondResult));
    }

    private static long SequenceOf(IResult result)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;
        return (long)value.GetType().GetProperty(SequencePropertyName)!.GetValue(value)!;
    }

    private sealed class TestDbContextFactory(DbContextOptions<ManagementDbContext> options)
        : IDbContextFactory<ManagementDbContext>
    {
        public ManagementDbContext CreateDbContext() => new(options);
    }
}
