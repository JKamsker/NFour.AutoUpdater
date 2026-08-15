using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Data;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NFour.AutoUpdater.Core;
using NFour.AutoUpdater.Repository;

namespace NFour.AutoUpdater.Server;

internal sealed class SequenceAllocator(
    IDbContextFactory<ManagementDbContext>? databaseFactory,
    ConcurrentDictionary<(string Repository, string Package, string Version), byte[]> packageVersions,
    ConcurrentDictionary<(string Product, string Release), byte[]> releases,
    ConcurrentDictionary<(string Product, string Channel), byte[]> channels,
    ConcurrentDictionary<(string Product, string Name), byte[]> revocations,
    ConcurrentDictionary<string, long> sequenceCounters,
    ConcurrentDictionary<string, ConcurrentDictionary<long, byte>> allocatedSequences)
{
    internal const string PackageScope = "package";
    internal const string ReleaseScope = "release";
    internal const string ChannelScope = "channel";
    internal const string RevocationScope = "revocation";

    private const string RevocationsDocumentName = "revocations";
    private const string AdvisoryLockSqlFormat = "SELECT pg_advisory_xact_lock({0})";
    private const char SequenceKeySeparator = '\0';
    private const char ScopeNameSeparator = ':';
    private const long EmptySequence = 0;
    private const long FirstSequence = 1;
    private const byte AllocatedMarker = 0;

    private readonly SemaphoreSlim _gate = new(1, 1);

    internal static bool IsSupportedScope(string scope) =>
        scope is PackageScope or ReleaseScope or ChannelScope or RevocationScope;

    internal async Task<long> AllocateAsync(
        string repository,
        string scope,
        string name,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = CreateKey(repository, scope, name);
            if (databaseFactory is not null)
                return await AllocateFromDatabaseAsync(repository, scope, name, cancellationToken).ConfigureAwait(false);

            var allocated = sequenceCounters.AddOrUpdate(
                key,
                _ => NextInMemory(repository, scope, name),
                (_, value) => checked(value + FirstSequence));
            allocatedSequences.GetOrAdd(key, _ => new ConcurrentDictionary<long, byte>())[allocated] = AllocatedMarker;
            return allocated;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask<bool> WasAllocatedAsync(
        string repository,
        string scope,
        string name,
        long sequence,
        CancellationToken cancellationToken)
    {
        if (sequence < FirstSequence)
            return false;
        if (databaseFactory is null)
            return allocatedSequences.TryGetValue(CreateKey(repository, scope, name), out var values)
                && values.ContainsKey(sequence);

        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await database.SequenceClaims.AnyAsync(
                row => row.RepositoryId == repository
                    && row.Scope == scope
                    && row.Name == name
                    && row.Value == sequence,
                cancellationToken).ConfigureAwait(false))
            return true;

        var reservation = await database.SequenceReservations
            .FindAsync([repository, scope, name], cancellationToken)
            .ConfigureAwait(false);
        return reservation is not null
            && reservation.AllocatedSequencesJson is null
            && sequence < reservation.NextValue;
    }

    private long NextInMemory(string repository, string scope, string name)
    {
        var maximum = scope switch
        {
            PackageScope => packageVersions
                .Where(item => item.Key.Repository == repository && item.Key.Package == name)
                .Select(item => RepositoryJson.DeserializeManifest(item.Value).Sequence)
                .DefaultIfEmpty(EmptySequence)
                .Max(),
            ReleaseScope => releases
                .Where(item => item.Key.Product == name)
                .Select(item => DeserializePayload<ReleaseLock>(item.Value).Sequence)
                .DefaultIfEmpty(EmptySequence)
                .Max(),
            ChannelScope => channels
                .Where(item => item.Key.Product + ScopeNameSeparator + item.Key.Channel == name)
                .Select(item => DeserializePayload<ChannelPointer>(item.Value).ChannelSequence)
                .DefaultIfEmpty(EmptySequence)
                .Max(),
            RevocationScope => revocations
                .Where(item => item.Key.Product == name && item.Key.Name == RevocationsDocumentName)
                .Select(item => DeserializePayload<RevocationDocument>(item.Value).RevocationSequence)
                .DefaultIfEmpty(EmptySequence)
                .Max(),
            _ => EmptySequence
        };
        return checked(maximum + FirstSequence);
    }

    private async Task<long> AllocateFromDatabaseAsync(
        string repository,
        string scope,
        string name,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var database = await databaseFactory!.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                await using var transaction = await database.Database
                    .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                    .ConfigureAwait(false);
                var sequenceLockId = CreateLockId(repository, scope, name);
                var lockCommand = FormattableStringFactory.Create(AdvisoryLockSqlFormat, sequenceLockId);
                await database.Database.ExecuteSqlInterpolatedAsync(lockCommand, cancellationToken).ConfigureAwait(false);

                var row = await database.SequenceReservations
                    .FindAsync([repository, scope, name], cancellationToken)
                    .ConfigureAwait(false);
                long allocated;
                if (row is null)
                {
                    var current = await FindCurrentDatabaseSequenceAsync(database, repository, scope, name, cancellationToken).ConfigureAwait(false);
                    allocated = checked(current + FirstSequence);
                    database.SequenceReservations.Add(new SequenceReservationRow
                    {
                        RepositoryId = repository,
                        Scope = scope,
                        Name = name,
                        NextValue = checked(allocated + FirstSequence)
                    });
                }
                else
                {
                    allocated = row.NextValue;
                    row.NextValue = checked(allocated + FirstSequence);
                }

                database.SequenceClaims.Add(new SequenceClaimRow
                {
                    RepositoryId = repository,
                    Scope = scope,
                    Name = name,
                    Value = allocated,
                    AllocatedAt = DateTimeOffset.UtcNow
                });
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return allocated;
            }
            catch (Exception exception) when (
                attempt < DatabaseConcurrencyPolicy.MaximumRetries
                && DatabaseConcurrencyPolicy.IsTransient(exception))
            {
                await DatabaseConcurrencyPolicy.DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<long> FindCurrentDatabaseSequenceAsync(
        ManagementDbContext database,
        string repository,
        string scope,
        string name,
        CancellationToken cancellationToken) => scope switch
    {
        PackageScope => await database.PackageVersions
            .Where(row => row.RepositoryId == repository && row.PackageId == name)
            .Select(row => (long?)row.Sequence)
            .MaxAsync(cancellationToken).ConfigureAwait(false) ?? EmptySequence,
        ReleaseScope => await database.Releases
            .Where(row => row.RepositoryId == repository && row.ProductId == name)
            .Select(row => (long?)row.Sequence)
            .MaxAsync(cancellationToken).ConfigureAwait(false) ?? EmptySequence,
        ChannelScope => await database.Channels
            .Where(row => row.RepositoryId == repository && row.ProductId + ScopeNameSeparator + row.Channel == name)
            .Select(row => (long?)row.ChannelSequence)
            .MaxAsync(cancellationToken).ConfigureAwait(false) ?? EmptySequence,
        RevocationScope => EmptySequence,
        _ => EmptySequence
    };

    private static T DeserializePayload<T>(byte[] envelope) =>
        SignedDocument.DeserializePayload<T>(
            Base64Url.Decode(SignedDocument.DeserializeEnvelope(envelope).Payload));

    private static string CreateKey(string repository, string scope, string name) =>
        string.Concat(repository, SequenceKeySeparator, scope, SequenceKeySeparator, name);

    private static long CreateLockId(string repository, string scope, string name)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(CreateKey(repository, scope, name)));
        return BinaryPrimitives.ReadInt64LittleEndian(digest);
    }
}
