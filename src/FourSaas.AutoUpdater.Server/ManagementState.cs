using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Data;
using FourSaas.AutoUpdater.Core;
using FourSaas.AutoUpdater.Storage;
using FourSaas.AutoUpdater.Storage.Local;
using FourSaas.AutoUpdater.Repository;
using FourSaas.AutoUpdater.Publishing;
using Microsoft.EntityFrameworkCore;

namespace FourSaas.AutoUpdater.Server;

public sealed class ManagementState
{
    private sealed record SessionState(string Id, string Repository, DateTimeOffset ExpiresAt, int MaxObjects, long MaxTotalBytes, int ObjectCount, long TotalBytes, bool Sealed);
    private sealed record GrantState(string Id, string SessionId, string Repository, ObjectKey StagingKey, ContentHash Digest, long Length, DateTimeOffset ExpiresAt, bool Used);
    private readonly IWritableObjectStore? _store;
    private readonly IWritableObjectStore? _stagingStore;
    private readonly RepositoryLayout _layout = new(new RepositoryLayoutTemplates());
    private readonly object _sealGate = new();
    private readonly SemaphoreSlim _placementGate = new(1, 1);
    private readonly string? _persistencePath;
    private readonly IDbContextFactory<ManagementDbContext>? _databaseFactory;
    private readonly string? _repositoryId;
    private readonly ConcurrentDictionary<string, long> _sequenceCounters = new(StringComparer.Ordinal);
    public ManagementState(IWritableObjectStore? store = null, string? persistencePath = null, IWritableObjectStore? stagingStore = null, IDbContextFactory<ManagementDbContext>? databaseFactory = null, string? repositoryId = null)
    {
        _store = store;
        _stagingStore = stagingStore ?? store;
        _persistencePath = string.IsNullOrWhiteSpace(persistencePath) ? null : Path.GetFullPath(persistencePath);
        _databaseFactory = databaseFactory;
        if (!string.IsNullOrWhiteSpace(repositoryId) && !Identifier.IsValid(repositoryId, "repositoryId", out var repositoryError)) throw new FormatException(repositoryError);
        _repositoryId = string.IsNullOrWhiteSpace(repositoryId) ? null : repositoryId;
        Load();
    }

    public bool IsConfiguredRepository(string repository) => _repositoryId is null || string.Equals(_repositoryId, repository, StringComparison.Ordinal);
    private string DatabaseRepositoryId => _repositoryId ?? "default";

    public async Task LoadDatabaseAsync(CancellationToken cancellationToken = default)
    {
        if (_databaseFactory is null) return;
        await using var database = await _databaseFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        foreach (var product in await database.Products.AsNoTracking().Where(x => x.RepositoryId == DatabaseRepositoryId).ToListAsync(cancellationToken).ConfigureAwait(false)) Products[product.ProductId] = new();
        foreach (var row in await database.PackageVersions.AsNoTracking().Where(x => x.RepositoryId == DatabaseRepositoryId).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_store is not null && PackageId.TryCreate(row.PackageId, out var packageId) && Identifier.IsValid(row.Version, 64))
            {
                var objectResult = await _store.OpenAsync(_layout.Package(packageId, new PackageVersion(row.Version, row.Sequence)), cancellationToken: cancellationToken).ConfigureAwait(false);
                if (objectResult is not null)
                {
                    await using (objectResult.ConfigureAwait(false))
                    {
                        var manifestBytes = await ReadAllAsync(objectResult.Content, cancellationToken).ConfigureAwait(false);
                        var packageKey = (row.RepositoryId, row.PackageId, row.Version);
                        PackageVersions[packageKey] = manifestBytes;
                        if (row.FileTableJson is not null) PackageFileTableRegistrations[packageKey] = Encoding.UTF8.GetBytes(row.FileTableJson);
                        if (row.Published) PublishedPackageVersions[packageKey] = true;
                    }
                }
            }
        }
        foreach (var row in await database.Releases.AsNoTracking().Where(x => x.RepositoryId == DatabaseRepositoryId).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_store is not null)
            {
                var objectResult = await _store.OpenAsync(_layout.Release(row.ProductId, row.ReleaseId), cancellationToken: cancellationToken).ConfigureAwait(false);
                if (objectResult is not null) { await using (objectResult.ConfigureAwait(false)) Releases[(row.ProductId, row.ReleaseId)] = await ReadAllAsync(objectResult.Content, cancellationToken).ConfigureAwait(false); }
            }
        }
        foreach (var row in await database.Channels.AsNoTracking().Where(x => x.RepositoryId == DatabaseRepositoryId).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_store is not null)
            {
                var objectResult = await _store.OpenAsync(_layout.Channel(row.ProductId, row.Channel), cancellationToken: cancellationToken).ConfigureAwait(false);
                if (objectResult is not null) { await using (objectResult.ConfigureAwait(false)) Channels[(row.ProductId, row.Channel)] = await ReadAllAsync(objectResult.Content, cancellationToken).ConfigureAwait(false); }
            }
        }
        foreach (var row in await database.ReleaseDrafts.AsNoTracking().Where(x => x.RepositoryId == DatabaseRepositoryId).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            ReleaseDrafts[row.DraftId] = Encoding.UTF8.GetBytes(row.PayloadJson);
            ReleaseDraftProducts[row.DraftId] = row.ProductId;
        }
        foreach (var row in await database.BlobPlacements.AsNoTracking().Where(x => x.RepositoryId == DatabaseRepositoryId && x.VerifiedAt != null).ToListAsync(cancellationToken).ConfigureAwait(false))
            if (ContentHash.TryParse(row.Algorithm + ":" + row.Hash, out var digest)) VerifiedBlobs[Scoped(DatabaseRepositoryId, digest)] = row.VerifiedAt!.Value;
        foreach (var row in await database.TrustedKeys.AsNoTracking().Where(x => x.RepositoryId == DatabaseRepositoryId).ToListAsync(cancellationToken).ConfigureAwait(false))
            try { TrustedKeys[row.KeyId] = Base64Url.Decode(row.PublicKey); } catch (FormatException) { }
        foreach (var row in await database.Revocations.AsNoTracking().Where(x => x.RepositoryId == DatabaseRepositoryId).ToListAsync(cancellationToken).ConfigureAwait(false))
            try { Revocations[(row.ProductId, "revocations")] = Convert.FromBase64String(row.EnvelopeJson); } catch (FormatException) { }
        foreach (var row in await database.PublishSessions.AsNoTracking().Where(x => x.RepositoryId == DatabaseRepositoryId && x.ExpiresAt > DateTimeOffset.UtcNow).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            PublishSessions[row.SessionId] = new SessionState(row.SessionId, row.RepositoryId, row.ExpiresAt, row.MaxObjects, row.MaxTotalBytes, row.ObjectCount, row.TotalBytes, row.Sealed);
            Sessions[row.SessionId] = new();
        }
        foreach (var row in await database.PublishGrants.AsNoTracking().Where(x => x.RepositoryId == DatabaseRepositoryId).ToListAsync(cancellationToken).ConfigureAwait(false))
            if (ContentHash.TryParse(row.Digest, out var digest) && Identifier.IsValid(row.RepositoryId, "repository", out _))
                Grants[row.GrantId] = new GrantState(row.GrantId, row.SessionId, row.RepositoryId, new ObjectKey(row.StagingKey), digest, row.Length, row.ExpiresAt, row.Used);
        foreach (var row in await database.AuditEvents.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken).ConfigureAwait(false))
            AuditEvents.Enqueue(new AuditEvent(row.At, row.Actor, row.Action, row.Resource, row.Outcome));
    }
    public string RepositoryBaseUrl { get; init; } = "";
    public ConcurrentDictionary<string, object> Products { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<(string Repository, string Package, string Version), byte[]> PackageVersions { get; } = new();
    public ConcurrentDictionary<(string Repository, string Package, string Version), byte[]> PackageFileTableRegistrations { get; } = new();
    public ConcurrentDictionary<(string Repository, string Package, string Version), bool> PublishedPackageVersions { get; } = new();
    public ConcurrentDictionary<string, byte[]> ReleaseDrafts { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, string> ReleaseDraftProducts { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, string> Yanks { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<(string Product, string Name), byte[]> Revocations { get; } = new();
    public ConcurrentDictionary<(string Product, string Channel), byte[]> Channels { get; } = new();
    public ConcurrentDictionary<(string Product, string Release), byte[]> Releases { get; } = new();
    public ConcurrentDictionary<string, object> Sessions { get; } = new(StringComparer.Ordinal);
    private ConcurrentDictionary<string, SessionState> PublishSessions { get; } = new(StringComparer.Ordinal);
    private ConcurrentDictionary<string, GrantState> Grants { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, DateTimeOffset> VerifiedBlobs { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, byte[]> TrustedKeys { get; } = new(StringComparer.Ordinal);
    public sealed record AuditEvent(DateTimeOffset At, string Actor, string Action, string Resource, string Outcome);
    public ConcurrentQueue<AuditEvent> AuditEvents { get; } = new();

    public void ConfigureTrustedKeys(IEnumerable<(string KeyId, byte[] PublicKey)> keys)
    {
        foreach (var (keyId, publicKey) in keys)
        {
            if (!Identifier.IsValid(keyId, "keyId", out var error)) throw new FormatException(error);
            if (publicKey.Length != 32) throw new FormatException($"Trusted key '{keyId}' is not a 32-byte Ed25519 public key.");
            TrustedKeys[keyId] = publicKey.ToArray();
        }
        Persist();
        PersistTrustedKeysAsync().GetAwaiter().GetResult();
    }
    public object OpenSession(string repository)
    {
        if (!Identifier.IsValid(repository, "repository", out var repositoryError)) throw new FormatException(repositoryError);
        if (!IsConfiguredRepository(repository)) throw new KeyNotFoundException($"Repository '{repository}' is not served by this control plane.");
        var id = Guid.NewGuid().ToString("N");
        var expires = DateTimeOffset.UtcNow.AddMinutes(15);
        PublishSessions[id] = new SessionState(id, repository, expires, 100_000, 1_000_000_000_000L, 0, 0, false);
        Sessions[id] = new();
        RecordAudit("publisher", "publish.session.open", $"{repository}/{id}", "accepted");
        Persist();
        UpsertSessionAsync(PublishSessions[id]).GetAwaiter().GetResult();
        return new { sessionId = id, repository, backendId = "configured", maxObjects = 100_000, maxTotalBytes = 1_000_000_000_000L, expiresAt = expires };
    }

    public string[] QueryBlobs(string repository, IEnumerable<string> requested) => !IsConfiguredRepository(repository) ? [] : requested.Where(x => ContentHash.TryParse(x, out var hash) && hash.Algorithm == HashAlgorithmId.Sha256 && VerifiedBlobs.ContainsKey(Scoped(repository, hash))).ToArray();

    public async Task<IResult> AllocateSequenceAsync(string repository, string scope, string name, CancellationToken cancellationToken = default)
    {
        if (!IsConfiguredRepository(repository) || !Identifier.IsValid(repository, "repository", out var repositoryError)) return Results.NotFound();
        if (scope is not ("package" or "release" or "channel") || string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl)) return Results.BadRequest(new { error = "invalid_sequence_scope" });
        long allocated;
        await _placementGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = repository + "\0" + scope + "\0" + name;
            if (_databaseFactory is not null)
            {
                await using var database = await _databaseFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
                var row = await database.SequenceReservations.FindAsync([repository, scope, name], cancellationToken).ConfigureAwait(false);
                if (row is null)
                {
                    var current = scope switch
                    {
                        "package" => await database.PackageVersions.Where(x => x.RepositoryId == repository && x.PackageId == name).Select(x => (long?)x.Sequence).MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0,
                        "release" => await database.Releases.Where(x => x.RepositoryId == repository && x.ProductId == name).Select(x => (long?)x.Sequence).MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0,
                        "channel" => await database.Channels.Where(x => x.RepositoryId == repository && x.ProductId + ":" + x.Channel == name).Select(x => (long?)x.ChannelSequence).MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0,
                        _ => 0
                    };
                    allocated = checked(current + 1);
                    database.SequenceReservations.Add(new SequenceReservationRow { RepositoryId = repository, Scope = scope, Name = name, NextValue = checked(allocated + 1) });
                }
                else { allocated = row.NextValue; row.NextValue = checked(allocated + 1); }
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                allocated = _sequenceCounters.AddOrUpdate(key, _ => NextInMemorySequence(repository, scope, name), (_, value) => checked(value + 1));
                _sequenceCounters[key] = checked(allocated + 1);
            }
        }
        finally { _placementGate.Release(); }
        RecordAudit("publisher", "sequence.allocate", $"{repository}/{scope}/{name}", "accepted");
        return Results.Ok(new { repository, scope, name, sequence = allocated });
    }

    private long NextInMemorySequence(string repository, string scope, string name)
    {
        var maximum = scope switch
        {
            "package" => PackageVersions.Where(x => x.Key.Repository == repository && x.Key.Package == name).Select(x => RepositoryJson.DeserializeManifest(x.Value).Sequence).DefaultIfEmpty(0).Max(),
            "release" => Releases.Where(x => x.Key.Product == name).Select(x => SignedDocument.DeserializePayload<ReleaseLock>(Base64Url.Decode(SignedDocument.DeserializeEnvelope(x.Value).Payload)).Sequence).DefaultIfEmpty(0).Max(),
            "channel" => Channels.Where(x => x.Key.Product + ":" + x.Key.Channel == name).Select(x => SignedDocument.DeserializePayload<ChannelPointer>(Base64Url.Decode(SignedDocument.DeserializeEnvelope(x.Value).Payload)).ChannelSequence).DefaultIfEmpty(0).Max(),
            _ => 0
        };
        return checked(maximum + 1);
    }

    public async Task<IResult> RecordTelemetryAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        byte[] bytes;
        try { bytes = await ReadAllAsync(request.Body, cancellationToken).ConfigureAwait(false); JsonRules.Validate(bytes); using var document = JsonDocument.Parse(bytes); }
        catch (Exception ex) when (ex is FormatException or JsonException) { return Results.BadRequest(new { error = "invalid_telemetry", detail = ex.Message }); }
        if (_databaseFactory is not null)
        {
            await using var database = await _databaseFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            database.Telemetry.Add(new TelemetryRow { At = DateTimeOffset.UtcNow, PayloadJson = Encoding.UTF8.GetString(bytes) });
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return Results.Accepted();
    }

    public async Task<IResult> CreateGrantsAsync(string sessionId, HttpRequest request, string? repository = null)
    {
        if (!PublishSessions.TryGetValue(sessionId, out var session) || session.Sealed || session.ExpiresAt <= DateTimeOffset.UtcNow) return Results.NotFound();
        if (repository is not null && (!IsConfiguredRepository(repository) || !string.Equals(repository, session.Repository, StringComparison.Ordinal))) return Results.NotFound();
        await _placementGate.WaitAsync(request.HttpContext.RequestAborted).ConfigureAwait(false);
        try
        {
        using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return Results.BadRequest(new { error = "items_required" });
        var requested = new List<(ContentHash Hash, long Length)>();
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("sha256", out var hashElement) || !ContentHash.TryParse(hashElement.GetString(), out var hash) || hash.Algorithm != HashAlgorithmId.Sha256 || !item.TryGetProperty("storedLength", out var lengthElement) || !lengthElement.TryGetInt64(out var length) || length < 0)
                return Results.BadRequest(new { error = "invalid_grant_request" });
            requested.Add((hash, length));
        }
        if (session.ObjectCount + requested.Count > session.MaxObjects || session.TotalBytes + requested.Sum(x => x.Length) > session.MaxTotalBytes) return Results.BadRequest(new { error = "session_quota_exceeded" });
        var expires = DateTimeOffset.UtcNow.AddMinutes(15);
        var response = new List<object>(requested.Count);
        foreach (var item in requested)
        {
            var grantId = Guid.NewGuid().ToString("N");
            var stagingKey = new ObjectKey($"_staging/{sessionId}/{grantId}");
            Grants[grantId] = new GrantState(grantId, sessionId, session.Repository, stagingKey, item.Hash, item.Length, expires, false);
            Uri? uploadUri = null;
            if (_stagingStore is IPresigningStore presigning)
                uploadUri = await presigning.CreateUploadUriAsync(new UploadGrantDescriptor(stagingKey, item.Hash, item.Length, expires), request.HttpContext.RequestAborted).ConfigureAwait(false);
            var requiredHeaders = _stagingStore is IUploadHeaderProvider headerProvider
                ? headerProvider.GetRequiredUploadHeaders(new UploadGrantDescriptor(stagingKey, item.Hash, item.Length, expires)).Select(x => new { name = x.Key, value = x.Value }).ToArray()
                : Array.Empty<object>();
            var localPath = string.Equals(Environment.GetEnvironmentVariable("FOURSUP_ALLOW_LOCAL_PATH_GRANTS"), "1", StringComparison.Ordinal) && _stagingStore is LocalObjectStore local
                ? Path.Combine(local.Root, stagingKey.Value.Replace('/', Path.DirectorySeparatorChar))
                : null;
            response.Add(new { grantId, stagingKey = stagingKey.Value, expectedDigest = item.Hash.ToString(), expectedLength = item.Length, expiresAt = expires, uploadUri, localPath, requiredHeaders, enforcement = _stagingStore is IPresigningStore ? "storageEnforced" : "serverVerified" });
        }
        PublishSessions[sessionId] = session with { ObjectCount = session.ObjectCount + requested.Count, TotalBytes = session.TotalBytes + requested.Sum(x => x.Length) };
        RecordAudit(request.HttpContext.Items["4sup-role"]?.ToString() ?? "unknown", "publish.grants.mint", $"{session.Repository}/{sessionId}", "accepted");
        Persist();
        await UpsertSessionAsync(PublishSessions[sessionId], request.HttpContext.RequestAborted).ConfigureAwait(false);
        foreach (var grant in Grants.Values.Where(x => x.SessionId == sessionId)) await UpsertGrantAsync(grant, request.HttpContext.RequestAborted).ConfigureAwait(false);
        return Results.Ok(new { items = response });
        }
        finally { _placementGate.Release(); }
    }

    public async Task<IResult> SealSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!PublishSessions.TryGetValue(sessionId, out var session)) return Results.NotFound();
        if (session.Sealed) return Results.Conflict();
        if (session.ExpiresAt <= DateTimeOffset.UtcNow) return Results.BadRequest(new { error = "session_expired" });
        if (!PublishSessions.TryUpdate(sessionId, session with { Sealed = true }, session)) return Results.Conflict();
        Persist();
        await UpsertSessionAsync(PublishSessions[sessionId], cancellationToken).ConfigureAwait(false);
        var stagingStore = _stagingStore;
        if (_store is null || stagingStore is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var verified = new List<string>();
        try
        {
            foreach (var grant in Grants.Values.Where(x => x.SessionId == sessionId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (grant.Used)
                {
                    verified.Add(grant.Digest.ToString());
                    continue;
                }
                var head = await stagingStore.HeadAsync(grant.StagingKey, cancellationToken).ConfigureAwait(false);
                if (head is null || head.Length != grant.Length) throw new InvalidDataException($"Staged grant '{grant.Id}' is missing or has the wrong length.");
                var read = await stagingStore.OpenAsync(grant.StagingKey, cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException(grant.StagingKey.Value);
                await using (read.ConfigureAwait(false))
                {
                    var actual = await ContentHash.ComputeAsync(read.Content, HashAlgorithmId.Sha256, cancellationToken).ConfigureAwait(false);
                    if (actual != grant.Digest) throw new CryptographicException($"Staged grant '{grant.Id}' failed its SHA-256 verification.");
                }
                var destination = _layout.Blob(grant.Digest);
                if (_store is IServerSideVerifier destinationVerifier && await _store.HeadAsync(destination, cancellationToken).ConfigureAwait(false) is not null)
                {
                    if (!await destinationVerifier.VerifyAsync(destination, grant.Digest, cancellationToken).ConfigureAwait(false)) throw new CryptographicException($"Immutable destination '{destination}' contains different bytes.");
                }
                else if (ReferenceEquals(stagingStore, _store) && _store is IServerSideCopyStore copy) await copy.CopyAsync(grant.StagingKey, destination, overwrite: false, cancellationToken).ConfigureAwait(false);
                else if (_store is IContentAddressedWriteStore addressed)
                {
                    var body = await stagingStore.OpenAsync(grant.StagingKey, cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException(grant.StagingKey.Value);
                    await using (body.ConfigureAwait(false))
                        if (!await addressed.PutIfAbsentAsync(destination, grant.Digest, body.Content, grant.Length, cancellationToken).ConfigureAwait(false)) throw new IOException($"Immutable destination race for '{destination}'.");
                }
                else
                {
                    var body = await stagingStore.OpenAsync(grant.StagingKey, cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException(grant.StagingKey.Value);
                    await using (body.ConfigureAwait(false))
                    {
                        if (_store is IConditionalWriteStore conditional)
                        {
                            if (!await conditional.PutIfAbsentAsync(destination, body.Content, grant.Length, cancellationToken).ConfigureAwait(false)) throw new IOException($"Immutable destination race for '{destination}'.");
                        }
                        else throw new IOException("The destination backend must support immutable conditional writes for promotion.");
                    }
                }
                // A server-side verifier can check an immutable object without downloading it, but
                // every promotion still gets a final read/hash barrier.  This matters for local,
                // conditional-write, and copy implementations whose write primitive is not itself
                // content-addressed; a successful write alone is not proof that the destination bytes
                // match the grant.
                var promoted = await _store.OpenAsync(destination, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (promoted is null) throw new FileNotFoundException(destination.Value);
                await using (promoted.ConfigureAwait(false))
                {
                    var promotedHead = await _store.HeadAsync(destination, cancellationToken).ConfigureAwait(false);
                    if (promotedHead is null || promotedHead.Length != grant.Length) throw new CryptographicException($"Promoted object '{destination}' has the wrong length.");
                    var actual = await ContentHash.ComputeAsync(promoted.Content, grant.Digest.Algorithm, cancellationToken).ConfigureAwait(false);
                    if (actual != grant.Digest) throw new CryptographicException($"Promoted object '{destination}' failed verification.");
                }
                VerifiedBlobs[Scoped(session.Repository, grant.Digest)] = DateTimeOffset.UtcNow;
                Grants[grant.Id] = grant with { Used = true };
                Persist();
                await UpsertGrantAsync(Grants[grant.Id], cancellationToken).ConfigureAwait(false);
                await UpsertVerifiedBlobAsync(session.Repository, grant.Digest, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                verified.Add(grant.Digest.ToString());
            }
        }
        catch
        {
            PublishSessions.TryUpdate(sessionId, session with { Sealed = false }, session with { Sealed = true });
            Persist();
            if (PublishSessions.TryGetValue(sessionId, out var resetSession)) await UpsertSessionAsync(resetSession, CancellationToken.None).ConfigureAwait(false);
            return Results.Conflict();
        }
        RecordAudit("publisher", "publish.session.seal", $"{session.Repository}/{sessionId}", "accepted");
        Persist();
        return Results.Ok(new { sealedSession = true, verified = verified.ToArray(), promoted = verified.ToArray() });
    }

    public IResult SealSession(string sessionId) => SealSessionAsync(sessionId).GetAwaiter().GetResult();

    public async Task<IResult> RegisterPackageVersionAsync(string repository, string package, HttpRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsConfiguredRepository(repository)) return Results.NotFound();
        if (!Identifier.IsValid(repository, "repository", out var repositoryError) || !PackageId.TryCreate(package, out var packageId)) return Results.BadRequest(new { error = repositoryError ?? "invalid_package" });
        var bytes = await ReadAllAsync(request.Body, cancellationToken).ConfigureAwait(false);
        PackageManifest manifest;
        try { manifest = RepositoryJson.DeserializeManifest(bytes); }
        catch (Exception ex) when (ex is FormatException or JsonException) { return Results.BadRequest(new { error = "invalid_manifest", detail = ex.Message }); }
        if (manifest.Id != packageId) return Results.BadRequest(new { error = "package_identity_mismatch" });
        await _placementGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        var key = (repository, package, manifest.Version.Label);
        if (PackageVersions.TryGetValue(key, out var existing)) return existing.AsSpan().SequenceEqual(bytes) ? Results.Ok(new { package = package, version = manifest.Version.Label, immutable = true }) : Results.Conflict();
        var maximumSequence = PackageVersions
            .Where(x => string.Equals(x.Key.Repository, repository, StringComparison.Ordinal) && string.Equals(x.Key.Package, package, StringComparison.Ordinal))
            .Select(x => RepositoryJson.DeserializeManifest(x.Value).Sequence)
            .DefaultIfEmpty(0)
            .Max();
        if (manifest.Sequence <= maximumSequence) return Results.Conflict(new { error = "package_sequence_not_monotonic", maximum = maximumSequence });
        if (_store is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var objectKey = _layout.Package(packageId, manifest.Version);
        if (_store is not IConditionalWriteStore conditional) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        await using var content = new MemoryStream(bytes, writable: false);
        if (!await conditional.PutIfAbsentAsync(objectKey, content, bytes.LongLength, cancellationToken).ConfigureAwait(false)) return Results.Conflict();
        PackageVersions[key] = bytes;
        Products[manifest.Id.Value] = new();
        Persist();
        await UpsertPackageVersionAsync(repository, manifest, bytes, cancellationToken).ConfigureAwait(false);
        RecordAudit(request.HttpContext.Items["4sup-role"]?.ToString() ?? "unknown", "package.register", $"{repository}/{package}/{manifest.Version.Label}", "accepted");
        return Results.Created($"/api/v1/repositories/{repository}/packages/{package}/versions/{manifest.Version.Label}", new { package, version = manifest.Version.Label, immutable = false });
        }
        finally { _placementGate.Release(); }
    }

    public async Task<IResult> RegisterFileTableAsync(string repository, string package, string version, HttpRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsConfiguredRepository(repository)) return Results.NotFound();
        if (!PackageVersions.TryGetValue((repository, package, version), out var manifestBytes)) return Results.NotFound();
        try
        {
            var manifest = RepositoryJson.DeserializeManifest(manifestBytes);
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            JsonRules.Validate(JsonSerializer.SerializeToUtf8Bytes(document.RootElement));
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("shards", out var shards) || shards.ValueKind != JsonValueKind.Array) return Results.BadRequest(new { error = "shards_required" });
            var declared = new List<FileTableShardRef>();
            foreach (var item in shards.EnumerateArray())
            {
                if (!item.TryGetProperty("index", out var index) || !index.TryGetInt32(out var shardIndex) || !item.TryGetProperty("digest", out var digest) || !ContentHash.TryParse(digest.GetString(), out var shardDigest) || shardDigest.Algorithm != HashAlgorithmId.Sha256 || !item.TryGetProperty("count", out var count) || !count.TryGetInt32(out var shardCount) || shardCount < 0 || !item.TryGetProperty("size", out var size) || !size.TryGetInt64(out var shardSize) || shardSize < 0)
                    return Results.BadRequest(new { error = "invalid_file_table_shard" });
                declared.Add(new FileTableShardRef { Index = shardIndex, Digest = shardDigest, Count = shardCount, Size = shardSize });
            }
            if (declared.Count != manifest.FileTable.ShardCount || !declared.OrderBy(x => x.Index).SequenceEqual(manifest.FileTable.Shards.OrderBy(x => x.Index))) return Results.Conflict(new { error = "file_table_header_does_not_match_manifest" });
            if (declared.Any(x => !VerifiedBlobs.ContainsKey(Scoped(repository, x.Digest)))) return Results.Conflict(new { error = "file_table_shard_not_verified" });
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { format = manifest.FileTable.Format, shardCount = manifest.FileTable.ShardCount, digest = manifest.FileTable.Digest.ToString(), shards = declared.OrderBy(x => x.Index).Select(x => new { index = x.Index, digest = x.Digest.ToString(), count = x.Count, size = x.Size }).ToArray() });
            PackageFileTableRegistrations[(repository, package, version)] = bytes;
            Persist();
            await UpsertFileTableAsync(repository, package, version, bytes, cancellationToken).ConfigureAwait(false);
            RecordAudit("publisher", "package.file-table.register", $"{repository}/{package}/{version}", "accepted");
            return Results.Ok(new { registered = shards.GetArrayLength(), package, version });
        }
        catch (JsonException ex) { return Results.BadRequest(new { error = "invalid_file_table", detail = ex.Message }); }
    }

    public async Task<IResult> PublishPackageVersion(string repository, string package, string version)
    {
        if (!IsConfiguredRepository(repository)) return Results.NotFound();
        if (!PackageVersions.ContainsKey((repository, package, version))) return Results.NotFound();
        if (!PackageFileTableRegistrations.ContainsKey((repository, package, version))) return Results.Conflict(new { error = "file_table_not_registered" });
        PublishedPackageVersions[(repository, package, version)] = true;
        Persist();
        await MarkPackagePublishedAsync(repository, package, version).ConfigureAwait(false);
        await RebuildPackageProjectionAsync(repository, package).ConfigureAwait(false);
        RecordAudit("publisher", "package.publish", $"{repository}/{package}/{version}", "accepted");
        return Results.Ok(new { package, version, state = "published" });
    }

    public async Task<IResult> CreateReleaseDraftAsync(string product, HttpRequest request, CancellationToken cancellationToken = default)
    {
        if (!Identifier.IsValid(product, "productId", out var error)) return Results.BadRequest(new { error });
        var bytes = await ReadAllAsync(request.Body, cancellationToken).ConfigureAwait(false);
        try { JsonRules.Validate(bytes); using var _ = JsonDocument.Parse(bytes); }
        catch (Exception ex) when (ex is FormatException or JsonException) { return Results.BadRequest(new { error = "invalid_release_draft", detail = ex.Message }); }
        var id = Guid.NewGuid().ToString("N");
        ReleaseDrafts[id] = bytes;
        ReleaseDraftProducts[id] = product;
        Persist();
        await UpsertReleaseDraftAsync(id, product, bytes, cancellationToken).ConfigureAwait(false);
        RecordAudit("publisher", "release.draft.create", $"{product}/{id}", "accepted");
        return Results.Created($"/api/v1/products/{product}/releases/drafts/{id}", new { draftId = id, product });
    }

    public IResult CheckReleaseDraft(string product, string draft)
    {
        if (!ReleaseDrafts.ContainsKey(draft)) return Results.NotFound();
        if (!ReleaseDraftProducts.TryGetValue(draft, out var storedProduct) || !string.Equals(storedProduct, product, StringComparison.Ordinal)) return Results.NotFound();
        try
        {
            var release = SignedDocument.DeserializePayload<ReleaseLock>(ReleaseDrafts[draft]);
            var manifests = PackageVersions.Values.Select(x => RepositoryJson.DeserializeManifest(x)).GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.Last());
            var diagnostics = new PublishGate().CheckAsync(release, manifests).GetAwaiter().GetResult();
            RecordAudit("publisher", "release.draft.check", $"{product}/{draft}", !diagnostics.Any(x => x.IsError) ? "accepted" : "rejected");
            return Results.Ok(new { draftId = draft, product, valid = !diagnostics.Any(x => x.IsError), diagnostics });
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidDataException)
        {
            return Results.BadRequest(new { error = "invalid_release_draft", detail = ex.Message });
        }
    }

    public async Task<IResult> RegisterReleaseDraftCoverageAsync(string product, string draft, HttpRequest request, CancellationToken cancellationToken = default)
    {
        if (!ReleaseDrafts.TryGetValue(draft, out var draftBytes) || !ReleaseDraftProducts.TryGetValue(draft, out var storedProduct) || !string.Equals(storedProduct, product, StringComparison.Ordinal)) return Results.NotFound();
        try
        {
            var release = SignedDocument.DeserializePayload<ReleaseLock>(draftBytes);
            var coverageBytes = await ReadAllAsync(request.Body, cancellationToken).ConfigureAwait(false);
            var coverage = RepositoryJson.Deserialize<CoverageDocument>(coverageBytes, rejectUnknownFields: true);
            if (coverage.Digest is not { } digest || digest != release.CoverageDigest || ContentHash.Compute(CoverageGenerator.SerializeForDigest(coverage)) != release.CoverageDigest) return Results.Conflict(new { error = "coverage_digest_mismatch" });
            if (_store is not IConditionalWriteStore conditional) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            await using var body = new MemoryStream(coverageBytes, writable: false);
            if (!await conditional.PutIfAbsentAsync(_layout.Coverage(product, release.ReleaseId), body, coverageBytes.LongLength, cancellationToken).ConfigureAwait(false)) return Results.Conflict(new { error = "coverage_already_exists" });
            RecordAudit("publisher", "release.draft.coverage", $"{product}/{draft}", "accepted");
            return Results.Ok(new { registered = true, digest = digest.ToString() });
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidDataException) { return Results.BadRequest(new { error = "invalid_coverage", detail = ex.Message }); }
    }

    public async Task<IResult> QuarantineBlobAsync(string hash, CancellationToken cancellationToken = default)
    {
        if (!ContentHash.TryParse(hash, out var digest) || digest.Algorithm != HashAlgorithmId.Sha256) return Results.BadRequest(new { error = "invalid_hash" });
        if (_store is not IWritableObjectStore writable) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var source = _layout.Blob(digest);
        var existing = await writable.OpenAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (existing is null) return Results.NotFound();
        await using (existing.ConfigureAwait(false))
        {
            await using var copy = new MemoryStream();
            await existing.Content.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            var bytes = copy.ToArray();
            if (ContentHash.Compute(bytes) != digest) return Results.Conflict(new { error = "blob_integrity_mismatch" });
            var trash = new ObjectKey($"_trash/{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}/{digest.ToString().Replace(':', '-')}");
            copy.Position = 0;
            await writable.PutAsync(trash, copy, bytes.LongLength, cancellationToken).ConfigureAwait(false);
            await writable.DeleteAsync(source, cancellationToken).ConfigureAwait(false);
        }
        foreach (var placement in VerifiedBlobs.Keys.Where(x => x.EndsWith("\0" + digest, StringComparison.Ordinal)).ToArray()) VerifiedBlobs.TryRemove(placement, out _);
        if (_databaseFactory is not null)
        {
            await using var database = await _databaseFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var placements = await database.BlobPlacements.Where(x => x.RepositoryId == DatabaseRepositoryId && x.Algorithm == "sha256" && x.Hash == digest.ToString().Split(':')[1]).ToListAsync(cancellationToken).ConfigureAwait(false);
            database.BlobPlacements.RemoveRange(placements);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        RecordAudit("operator", "blob.quarantine", hash, "accepted");
        Persist();
        return Results.Accepted(value: new { hash, quarantined = true });
    }

    public async Task<IResult> CollectGcAsync(string repository, bool dryRun, CancellationToken cancellationToken = default)
    {
        if (!IsConfiguredRepository(repository)) return Results.NotFound();
        if (_store is not IListableObjectStore listable) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        try
        {
            var (descriptor, layout) = await RepositoryFactory.LoadDescriptorAsync(_store, cancellationToken).ConfigureAwait(false);
            var releases = new List<ReleaseLock>();
            foreach (var bytes in Releases.Where(x => Products.ContainsKey(x.Key.Product) || x.Key.Product is not null).Select(x => x.Value))
            {
                var envelope = SignedDocument.DeserializeEnvelope(bytes);
                if (!SignedDocument.Verify(envelope, TrustedKeys, out var payload, out var error)) throw new CryptographicException(error);
                var release = SignedDocument.DeserializePayload<ReleaseLock>(payload);
                if (release.State == ReleaseState.Published) releases.Add(release);
            }
            var repositoryView = new StaticRepository(_store, descriptor);
            var result = await new GarbageCollector().CollectLiveAsync(listable, layout, releases, repositoryView, new GarbageCollectionOptions { DryRun = dryRun }, cancellationToken: cancellationToken).ConfigureAwait(false);
            RecordAudit("operator", "repository.gc", repository, dryRun ? "dry-run" : "accepted");
            return Results.Ok(new { marked = result.Marked.Select(x => x.Value).ToArray(), quarantined = result.Quarantined.Select(x => x.Value).ToArray(), deleted = result.Deleted.Select(x => x.Value).ToArray(), diagnostics = result.Diagnostics });
        }
        catch (FileNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or CryptographicException) { return Results.BadRequest(new { error = ex.Message }); }
    }

    public async Task<IResult> CollectStagingGcAsync(string repository, bool dryRun, CancellationToken cancellationToken = default)
    {
        if (!IsConfiguredRepository(repository)) return Results.NotFound();
        if (_stagingStore is not IListableObjectStore listable || _stagingStore is not IWritableObjectStore writable) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var now = DateTimeOffset.UtcNow;
        // A sealed session is still recoverable until its expiry: sealing is persisted
        // before promotion and a crash immediately afterwards must not lose its uploads.
        var active = PublishSessions.Values.Where(x => string.Equals(x.Repository, repository, StringComparison.Ordinal) && x.ExpiresAt > now).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var deleted = new List<string>();
        await foreach (var key in listable.ListAsync("_staging/", cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var parts = key.Value.Split('/');
            if (parts.Length < 3 || !string.Equals(parts[0], "_staging", StringComparison.Ordinal)) continue;
            if (active.Contains(parts[1])) continue;
            if (!dryRun) await writable.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            deleted.Add(key.Value);
        }
        RecordAudit("operator", "publish.staging-gc", repository, dryRun ? "dry-run" : "accepted");
        return Results.Ok(new { dryRun, deleted = deleted.ToArray(), count = deleted.Count });
    }

    public async Task<IResult> ReconcileAsync(string repository, CancellationToken cancellationToken = default)
    {
        if (!IsConfiguredRepository(repository)) return Results.NotFound();
        if (_store is not IListableObjectStore listable) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var alerts = new List<string>(); var repaired = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string>? databaseBlobs = null;
        if (_databaseFactory is not null)
        {
            await using var database = await _databaseFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            databaseBlobs = (await database.BlobPlacements.AsNoTracking().Where(x => x.RepositoryId == DatabaseRepositoryId && x.VerifiedAt != null).Select(x => x.Algorithm + ":" + x.Hash).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        }
        await foreach (var key in listable.ListAsync("blobs/", cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var name = key.Value.Split('/').LastOrDefault();
            if (name is null || !ContentHash.TryParse("sha256:" + name, out var hash)) { alerts.Add($"unparseable:{key.Value}"); continue; }
            var read = await _store!.OpenAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (read is null) { alerts.Add($"missing:{key.Value}"); continue; }
            await using (read.ConfigureAwait(false))
            {
                var actual = await ContentHash.ComputeAsync(read.Content, hash.Algorithm, cancellationToken).ConfigureAwait(false);
                if (actual != hash) { alerts.Add($"corrupt:{key.Value}"); continue; }
            }
            if (databaseBlobs is not null && !databaseBlobs.Contains(hash.ToString())) { alerts.Add($"untracked:{key.Value}"); continue; }
            if (!VerifiedBlobs.ContainsKey(Scoped(repository, hash)))
            {
                var verifiedAt = DateTimeOffset.UtcNow;
                VerifiedBlobs[Scoped(repository, hash)] = verifiedAt;
                repaired.Add(key.Value);
                await UpsertVerifiedBlobAsync(repository, hash, verifiedAt, cancellationToken).ConfigureAwait(false);
            }
            seen.Add(hash.ToString());
        }
        // Reconciliation also checks the authoritative signed/control objects and
        // manifests. CAS integrity alone cannot detect a missing pointer or a stale
        // projection after a database/storage crash.
        async ValueTask CheckObjectAsync(ObjectKey key, byte[] expected, string label)
        {
            var read = await _store!.OpenAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (read is null) { alerts.Add($"missing:{label}:{key.Value}"); return; }
            await using (read.ConfigureAwait(false))
            {
                var actual = await ReadAllAsync(read.Content, cancellationToken).ConfigureAwait(false);
                if (!actual.AsSpan().SequenceEqual(expected)) alerts.Add($"stale:{label}:{key.Value}");
            }
        }
        foreach (var ((repo, package, version), bytes) in PackageVersions.Where(x => string.Equals(x.Key.Repository, repository, StringComparison.Ordinal)))
            if (PackageId.TryCreate(package, out var packageId) && Identifier.IsValid(version, "version", out _))
            {
                var manifest = RepositoryJson.DeserializeManifest(bytes);
                await CheckObjectAsync(_layout.Package(packageId, manifest.Version), bytes, "package").ConfigureAwait(false);
            }
        foreach (var ((product, release), bytes) in Releases)
        {
            await CheckObjectAsync(_layout.Release(product, release), bytes, "release").ConfigureAwait(false);
            try
            {
                var envelope = SignedDocument.DeserializeEnvelope(bytes);
                var lockFile = SignedDocument.DeserializePayload<ReleaseLock>(Base64Url.Decode(envelope.Payload));
                if (lockFile.State == ReleaseState.Published)
                {
                    var coverage = await _store.OpenAsync(_layout.Coverage(product, release), cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (coverage is null) alerts.Add($"missing:coverage:{product}/{release}"); else await coverage.DisposeAsync().ConfigureAwait(false);
                    var bundle = await _store.OpenAsync(_layout.ReleaseBundle(product, release), cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (bundle is null) alerts.Add($"missing:bundle:{product}/{release}"); else await bundle.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) { alerts.Add($"invalid:release:{product}/{release}:{ex.Message}"); }
        }
        foreach (var ((product, channel), bytes) in Channels) await CheckObjectAsync(_layout.Channel(product, channel), bytes, "channel").ConfigureAwait(false);
        foreach (var ((product, name), bytes) in Revocations.Where(x => string.Equals(x.Key.Name, "revocations", StringComparison.Ordinal))) await CheckObjectAsync(_layout.Revocations(product), bytes, "revocation").ConfigureAwait(false);
        Persist();
        RecordAudit("operator", "repository.reconcile", repository, alerts.Count == 0 ? "accepted" : "alerted");
        return Results.Ok(new { repaired = repaired.ToArray(), alerts = alerts.ToArray(), scanned = seen.Count });
    }

    public async Task<IResult> PlaceSignedAsync(string expectedType, string product, string name, HttpRequest request, ConcurrentDictionary<(string, string), byte[]> target)
    {
        using var memory = new MemoryStream(); await request.Body.CopyToAsync(memory, request.HttpContext.RequestAborted); var bytes = memory.ToArray();
        SignedEnvelope envelope;
        try { envelope = SignedDocument.DeserializeEnvelope(bytes); } catch (Exception ex) { return Results.BadRequest(new { error = "invalid_envelope", detail = ex.Message }); }
        if (!string.Equals(envelope.Type, expectedType, StringComparison.Ordinal)) return Results.BadRequest(new { error = "wrong_document_type" });
        if (TrustedKeys.IsEmpty) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (!SignedDocument.Verify(envelope, TrustedKeys, out var payload, out var verificationError)) return Results.BadRequest(new { error = "signature_invalid", detail = verificationError });
        try
        {
            if (expectedType == "channel-pointer")
            {
                var pointer = SignedDocument.DeserializePayload<ChannelPointer>(payload);
                if (pointer.SchemaVersion != 1 || !string.Equals(pointer.ProductId, product, StringComparison.Ordinal) || !string.Equals(pointer.Channel, name, StringComparison.Ordinal)) return Results.BadRequest(new { error = "route_identity_mismatch" });
                if (pointer.SupersedesChannelSequence >= pointer.ChannelSequence) return Results.Conflict();
            }
            else if (expectedType == "revocation")
            {
                var revocation = SignedDocument.DeserializePayload<RevocationDocument>(payload);
                if (revocation.SchemaVersion != 1 || !string.Equals(revocation.ProductId, product, StringComparison.Ordinal) || !revocation.Entries.Any(x => string.Equals(x.ReleaseId, name, StringComparison.Ordinal))) return Results.BadRequest(new { error = "route_identity_mismatch" });
            }
            else
            {
                var release = SignedDocument.DeserializePayload<ReleaseLock>(payload);
                if (release.SchemaVersion != 1 || !string.Equals(release.ProductId, product, StringComparison.Ordinal) || !string.Equals(release.ReleaseId, name, StringComparison.Ordinal)) return Results.BadRequest(new { error = "route_identity_mismatch" });
            }
        }
        catch (Exception ex) { return Results.BadRequest(new { error = "payload_invalid", detail = ex.Message }); }
        if (expectedType == "release-lock")
        {
            try
            {
                var release = SignedDocument.DeserializePayload<ReleaseLock>(payload);
                if (release.State == ReleaseState.Published) await ValidateReleaseArtifactsAsync(product, name, release, request.HttpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (FileNotFoundException ex) { return Results.Conflict(new { error = "release_artifact_missing", detail = ex.Message }); }
            catch (Exception ex) when (ex is FormatException or JsonException or InvalidDataException or CryptographicException) { return Results.BadRequest(new { error = "release_artifact_invalid", detail = ex.Message }); }
        }
        await _placementGate.WaitAsync(request.HttpContext.RequestAborted).ConfigureAwait(false);
        try
        {
            var storageName = expectedType == "revocation" ? "revocations" : name;
            target.TryGetValue((product, storageName), out var previous);
            if (expectedType == "release-lock" && previous is not null && !previous.AsSpan().SequenceEqual(bytes)) return Results.Conflict();
            if (expectedType == "release-lock" && (previous is null || !previous.AsSpan().SequenceEqual(bytes)))
            {
                var candidate = SignedDocument.DeserializePayload<ReleaseLock>(payload);
                var maximumSequence = Releases
                    .Where(x => string.Equals(x.Key.Product, product, StringComparison.Ordinal))
                    .Select(x => SignedDocument.DeserializePayload<ReleaseLock>(Base64Url.Decode(SignedDocument.DeserializeEnvelope(x.Value).Payload)).Sequence)
                    .DefaultIfEmpty(0)
                    .Max();
                if (candidate.Sequence <= maximumSequence) return Results.Conflict(new { error = "release_sequence_not_monotonic", maximum = maximumSequence });
            }
            if (expectedType == "channel-pointer" && previous is not null)
            {
                try
                {
                    var oldEnvelope = SignedDocument.DeserializeEnvelope(previous);
                    if (!SignedDocument.Verify(oldEnvelope, TrustedKeys, out var oldPayload, out _)) return Results.Conflict();
                    var oldPointer = SignedDocument.DeserializePayload<ChannelPointer>(oldPayload);
                    var newPointer = SignedDocument.DeserializePayload<ChannelPointer>(payload);
                    if (newPointer.ChannelSequence <= oldPointer.ChannelSequence || newPointer.SupersedesChannelSequence != oldPointer.ChannelSequence || newPointer.ProductId != oldPointer.ProductId || newPointer.Channel != oldPointer.Channel) return Results.Conflict();
                }
                catch (Exception) { return Results.Conflict(); }
            }
            if (expectedType == "revocation" && previous is not null)
            {
                try
                {
                    var oldEnvelope = SignedDocument.DeserializeEnvelope(previous);
                    if (!SignedDocument.Verify(oldEnvelope, TrustedKeys, out var oldPayload, out _)) return Results.Conflict();
                    var oldDocument = SignedDocument.DeserializePayload<RevocationDocument>(oldPayload);
                    var newDocument = SignedDocument.DeserializePayload<RevocationDocument>(payload);
                    if (newDocument.RevocationSequence <= oldDocument.RevocationSequence) return Results.Conflict();
                }
                catch (Exception) { return Results.Conflict(); }
            }

            // The server stores the verified envelope byte-for-byte. It has no signing primitive and never
            // constructs a release lock or pointer. Channel promotion is a storage CAS as well as an
            // in-memory CAS, so a concurrent operator cannot publish a projection that static clients do not see.
            if (_store is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var layoutKey = expectedType == "channel-pointer" ? _layout.Channel(product, name) : expectedType == "revocation" ? _layout.Revocations(product) : _layout.Release(product, name);
            var head = await _store.HeadAsync(layoutKey, request.HttpContext.RequestAborted).ConfigureAwait(false);
            if (previous is null)
            {
                if (head is not null) return Results.Conflict();
                if (_store is not IConditionalWriteStore conditional) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                await using var content = new MemoryStream(bytes, writable: false);
                if (!await conditional.PutIfAbsentAsync(layoutKey, content, bytes.LongLength, request.HttpContext.RequestAborted).ConfigureAwait(false)) return Results.Conflict();
            }
            else
            {
                if (head is null || head.Validator is null) return Results.Conflict();
                var existing = await _store.OpenAsync(layoutKey, cancellationToken: request.HttpContext.RequestAborted).ConfigureAwait(false);
                if (existing is null) return Results.Conflict();
                await using (existing.ConfigureAwait(false))
                {
                    var existingBytes = await ReadAllAsync(existing.Content, request.HttpContext.RequestAborted).ConfigureAwait(false);
                    if (!existingBytes.AsSpan().SequenceEqual(previous)) return Results.Conflict();
                }
                if (_store is not IConditionalWriteStore conditional) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                await using var content = new MemoryStream(bytes, writable: false);
                if (!await conditional.CompareAndSwapAsync(layoutKey, head.Validator, content, bytes.LongLength, request.HttpContext.RequestAborted).ConfigureAwait(false)) return Results.Conflict();
            }
            if (previous is not null)
            {
                if (!target.TryUpdate((product, storageName), bytes, previous)) return Results.Conflict();
            }
            else if (!target.TryAdd((product, storageName), bytes)) return Results.Conflict();
            RecordAudit(request.HttpContext.Items["4sup-role"]?.ToString() ?? "unknown", expectedType switch { "channel-pointer" => "channel.place", "release-lock" => "release.place", _ => "revocation.place" }, $"{product}/{name}", "accepted");
            Persist();
            await UpsertPlacementAsync(expectedType, product, name, bytes, request.HttpContext.RequestAborted).ConfigureAwait(false);
            if (expectedType == "revocation") await UpsertRevocationAsync(product, bytes, request.HttpContext.RequestAborted).ConfigureAwait(false);
            if (expectedType == "release-lock") await RebuildReleaseProjectionsAsync(product, name, bytes, request.HttpContext.RequestAborted).ConfigureAwait(false);
            return Results.NoContent();
        }
        finally { _placementGate.Release(); }
    }

    private sealed record StoredState(
        Dictionary<string, byte[]> Channels,
        Dictionary<string, byte[]> Releases,
        string[] Products,
        Dictionary<string, SessionState> PublishSessions,
        Dictionary<string, GrantState> Grants,
        Dictionary<string, DateTimeOffset> VerifiedBlobs,
        Dictionary<string, byte[]> TrustedKeys,
        AuditEvent[]? AuditEvents,
        Dictionary<string, byte[]>? PackageVersions = null,
        Dictionary<string, byte[]>? PackageFileTableRegistrations = null,
        Dictionary<string, bool>? PublishedPackageVersions = null,
        Dictionary<string, byte[]>? ReleaseDrafts = null,
        Dictionary<string, string>? ReleaseDraftProducts = null,
        Dictionary<string, string>? Yanks = null,
        Dictionary<string, byte[]>? Revocations = null,
        string? RepositoryId = null);

    private void Persist()
    {
        if (_persistencePath is null) return;
        lock (_sealGate)
        {
            var state = new StoredState(
                Channels.ToDictionary(x => x.Key.Product + "\0" + x.Key.Channel, x => x.Value, StringComparer.Ordinal),
                Releases.ToDictionary(x => x.Key.Product + "\0" + x.Key.Release, x => x.Value, StringComparer.Ordinal),
                Products.Keys.ToArray(), PublishSessions.ToDictionary(), Grants.ToDictionary(), VerifiedBlobs.ToDictionary(), TrustedKeys.ToDictionary(), AuditEvents.ToArray(),
                PackageVersions.ToDictionary(x => x.Key.Repository + "\0" + x.Key.Package + "\0" + x.Key.Version, x => x.Value, StringComparer.Ordinal),
                PackageFileTableRegistrations.ToDictionary(x => x.Key.Repository + "\0" + x.Key.Package + "\0" + x.Key.Version, x => x.Value, StringComparer.Ordinal),
                PublishedPackageVersions.ToDictionary(x => x.Key.Repository + "\0" + x.Key.Package + "\0" + x.Key.Version, x => x.Value, StringComparer.Ordinal),
                ReleaseDrafts.ToDictionary(), ReleaseDraftProducts.ToDictionary(), Yanks.ToDictionary(), Revocations.ToDictionary(x => x.Key.Product + "\0" + x.Key.Name, x => x.Value, StringComparer.Ordinal), _repositoryId);
            var temporary = _persistencePath + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            Directory.CreateDirectory(Path.GetDirectoryName(_persistencePath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web)), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, _persistencePath, true);
        }
    }

    private void Load()
    {
        if (_persistencePath is null || !File.Exists(_persistencePath)) return;
        try
        {
            var state = JsonSerializer.Deserialize<StoredState>(File.ReadAllBytes(_persistencePath), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (state is null) return;
            if (_repositoryId is not null && !string.Equals(state.RepositoryId, _repositoryId, StringComparison.Ordinal))
                throw new InvalidDataException($"Management state is bound to repository '{state.RepositoryId ?? "unknown"}', not '{_repositoryId}'.");
            foreach (var (key, value) in state.Channels) { var split = key.Split('\0'); if (split.Length == 2) Channels[(split[0], split[1])] = value; }
            foreach (var (key, value) in state.Releases) { var split = key.Split('\0'); if (split.Length == 2) Releases[(split[0], split[1])] = value; }
            foreach (var product in state.Products) Products[product] = new();
            foreach (var (key, value) in state.PublishSessions) { PublishSessions[key] = value; Sessions[key] = new(); }
            foreach (var (key, value) in state.Grants) Grants[key] = value;
            foreach (var (key, value) in state.VerifiedBlobs) VerifiedBlobs[key] = value;
            foreach (var (key, value) in state.TrustedKeys) TrustedKeys[key] = value;
            if (state.AuditEvents is not null) foreach (var item in state.AuditEvents) AuditEvents.Enqueue(item);
            if (state.PackageVersions is not null)
                foreach (var (key, value) in state.PackageVersions)
                {
                    var split = key.Split('\0');
                    if (split.Length == 3) PackageVersions[(split[0], split[1], split[2])] = value;
                }
            if (state.PackageFileTableRegistrations is not null)
                foreach (var (key, value) in state.PackageFileTableRegistrations)
                {
                    var split = key.Split('\0');
                    if (split.Length == 3) PackageFileTableRegistrations[(split[0], split[1], split[2])] = value;
                }
            if (state.PublishedPackageVersions is not null)
                foreach (var (key, value) in state.PublishedPackageVersions)
                {
                    var split = key.Split('\0');
                    if (split.Length == 3) PublishedPackageVersions[(split[0], split[1], split[2])] = value;
                }
            if (state.ReleaseDrafts is not null) foreach (var (key, value) in state.ReleaseDrafts) ReleaseDrafts[key] = value;
            if (state.ReleaseDraftProducts is not null) foreach (var (key, value) in state.ReleaseDraftProducts) ReleaseDraftProducts[key] = value;
            if (state.Yanks is not null) foreach (var (key, value) in state.Yanks) Yanks[key] = value;
            if (state.Revocations is not null)
                foreach (var (key, value) in state.Revocations)
                {
                    var split = key.Split('\0');
                    if (split.Length == 2) Revocations[(split[0], split[1])] = value;
                }
        }
        catch (Exception ex) { throw new InvalidDataException($"Management state persistence is corrupt: {ex.Message}", ex); }
    }

    private static string Scoped(string repository, ContentHash hash) => repository + "\0" + hash;
    private void RecordAudit(string actor, string action, string resource, string outcome)
    {
        var item = new AuditEvent(DateTimeOffset.UtcNow, actor, action, resource, outcome);
        AuditEvents.Enqueue(item);
        Persist();
        UpsertAuditAsync(item).GetAwaiter().GetResult();
    }
    private static async ValueTask<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var bytes = new MemoryStream(); await stream.CopyToAsync(bytes, cancellationToken).ConfigureAwait(false); return bytes.ToArray();
    }

    private async Task UpsertPackageVersionAsync(string repository, PackageManifest manifest, byte[] bytes, CancellationToken cancellationToken)
    {
        if (_databaseFactory is null) return;
        await using var database = await _databaseFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var product = await database.Products.FindAsync([repository, manifest.Id.Value], cancellationToken).ConfigureAwait(false);
        if (product is null) database.Products.Add(new ProductRow { RepositoryId = repository, ProductId = manifest.Id.Value, CreatedAt = DateTimeOffset.UtcNow });
        var row = await database.PackageVersions.SingleOrDefaultAsync(x => x.RepositoryId == repository && x.PackageId == manifest.Id.Value && x.Version == manifest.Version.Label, cancellationToken).ConfigureAwait(false)
                  ?? new PackageVersionRow { RepositoryId = repository, PackageId = manifest.Id.Value, Version = manifest.Version.Label, ManifestDigest = string.Empty };
        row.Sequence = manifest.Sequence; row.ManifestDigest = ContentHash.Compute(bytes).ToString(); row.WhenJson = "{}"; row.Published = false;
        if (row is { } && database.Entry(row).State == EntityState.Detached) database.PackageVersions.Add(row);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RebuildPackageProjectionAsync(string repository, string package)
    {
        if (_store is null || !PackageId.TryCreate(package, out var packageId)) return;
        var manifests = PackageVersions
            .Where(x => string.Equals(x.Key.Repository, repository, StringComparison.Ordinal) && string.Equals(x.Key.Package, package, StringComparison.Ordinal))
            .Select(x => RepositoryJson.DeserializeManifest(x.Value))
            .ToArray();
        await new StaticProjectionWriter(_store, _layout).WritePackageIndexAsync(packageId, manifests).ConfigureAwait(false);
    }

    private async Task RebuildReleaseProjectionsAsync(string product, string releaseId, byte[] envelopeBytes, CancellationToken cancellationToken)
    {
        if (_store is null) return;
        var envelope = SignedDocument.DeserializeEnvelope(envelopeBytes);
        var release = SignedDocument.DeserializePayload<ReleaseLock>(Base64Url.Decode(envelope.Payload));
        if (release.State != ReleaseState.Published) return;
        await ValidateReleaseArtifactsAsync(product, releaseId, release, cancellationToken).ConfigureAwait(false);
        var manifests = new List<PackageManifest>();
        foreach (var pin in release.Packages)
        {
            var result = await _store.OpenAsync(_layout.Package(pin.Id, pin.Version), cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException(pin.ManifestPath);
            await using (result.ConfigureAwait(false))
            {
                var bytes = await ReadAllAsync(result.Content, cancellationToken).ConfigureAwait(false);
                if (ContentHash.Compute(bytes) != pin.ManifestDigest) throw new CryptographicException($"Manifest '{pin.Id}@{pin.Version}' failed its release pin.");
                manifests.Add(RepositoryJson.DeserializeManifest(bytes));
            }
        }
        var writer = new StaticProjectionWriter(_store, _layout);
        await writer.WriteReleaseBundleAsync(product, releaseId, envelopeBytes, manifests, cancellationToken).ConfigureAwait(false);
        var rows = new List<(ReleaseLock Lock, byte[] EnvelopeBytes)>();
        foreach (var item in Releases.Where(x => string.Equals(x.Key.Product, product, StringComparison.Ordinal)))
        {
            try
            {
                var candidateEnvelope = SignedDocument.DeserializeEnvelope(item.Value);
                var candidate = SignedDocument.DeserializePayload<ReleaseLock>(Base64Url.Decode(candidateEnvelope.Payload));
                if (candidate.State == ReleaseState.Published) rows.Add((candidate, item.Value));
            }
            catch (Exception) { }
        }
        if (!rows.Any(x => string.Equals(x.Lock.ReleaseId, releaseId, StringComparison.Ordinal))) rows.Add((release, envelopeBytes));
        await writer.WriteReleaseIndexAsync(product, rows, cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateReleaseArtifactsAsync(string product, string releaseId, ReleaseLock release, CancellationToken cancellationToken)
    {
        if (_store is null) throw new InvalidOperationException("A storage backend is required to validate release artifacts.");
        var coverageResult = await _store.OpenAsync(_layout.Coverage(product, releaseId), cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"Missing coverage document for release '{releaseId}'.");
        await using (coverageResult.ConfigureAwait(false))
        {
            var coverageBytes = await ReadAllAsync(coverageResult.Content, cancellationToken).ConfigureAwait(false);
            var coverage = RepositoryJson.Deserialize<CoverageDocument>(coverageBytes, rejectUnknownFields: true);
            if (!string.Equals(coverage.ReleaseId, release.ReleaseId, StringComparison.Ordinal)) throw new InvalidDataException("Coverage releaseId does not match the release lock.");
            if (coverage.Digest is not { } coverageDigest || coverageDigest != release.CoverageDigest) throw new CryptographicException("Coverage digest does not match the release lock.");
            if (ContentHash.Compute(CoverageGenerator.SerializeForDigest(coverage)) != release.CoverageDigest) throw new CryptographicException("Coverage bytes do not match the release lock coverageDigest.");
        }
    }

    private async Task UpsertFileTableAsync(string repository, string package, string version, byte[] bytes, CancellationToken cancellationToken)
    {
        if (_databaseFactory is null) return;
        await using var database = await _databaseFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await database.PackageVersions.SingleOrDefaultAsync(x => x.RepositoryId == repository && x.PackageId == package && x.Version == version, cancellationToken).ConfigureAwait(false);
        if (row is null) return;
        row.FileTableJson = Encoding.UTF8.GetString(bytes);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkPackagePublishedAsync(string repository, string package, string version)
    {
        if (_databaseFactory is null) return;
        await using var database = await _databaseFactory.CreateDbContextAsync().ConfigureAwait(false);
        var row = await database.PackageVersions.SingleOrDefaultAsync(x => x.RepositoryId == repository && x.PackageId == package && x.Version == version).ConfigureAwait(false);
        if (row is not null) { row.Published = true; await database.SaveChangesAsync().ConfigureAwait(false); }
    }

    private async Task UpsertReleaseDraftAsync(string draft, string product, byte[] bytes, CancellationToken cancellationToken)
    {
        if (_databaseFactory is null) return;
        await using var database = await _databaseFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await database.ReleaseDrafts.FindAsync([DatabaseRepositoryId, draft], cancellationToken).ConfigureAwait(false) ?? new ReleaseDraftRow { RepositoryId = DatabaseRepositoryId, DraftId = draft, ProductId = product, PayloadJson = string.Empty };
        row.RepositoryId = DatabaseRepositoryId;
        row.ProductId = product; row.PayloadJson = Encoding.UTF8.GetString(bytes); row.CreatedAt = DateTimeOffset.UtcNow;
        if (database.Entry(row).State == EntityState.Detached) database.ReleaseDrafts.Add(row);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertPlacementAsync(string type, string product, string name, byte[] bytes, CancellationToken cancellationToken)
    {
        if (_databaseFactory is null) return;
        await using var database = await _databaseFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (type == "release-lock")
        {
            var envelope = SignedDocument.DeserializeEnvelope(bytes);
            var release = SignedDocument.DeserializePayload<ReleaseLock>(Base64Url.Decode(envelope.Payload));
            var row = await database.Releases.FindAsync([DatabaseRepositoryId, product, name], cancellationToken).ConfigureAwait(false) ?? new ReleaseRow { RepositoryId = DatabaseRepositoryId, ProductId = product, ReleaseId = name, EnvelopeDigest = string.Empty, CoverageDigest = string.Empty };
            row.RepositoryId = DatabaseRepositoryId;
            row.Sequence = release.Sequence; row.EnvelopeDigest = ContentHash.Compute(bytes).ToString(); row.CoverageDigest = release.CoverageDigest.ToString(); row.Published = release.State == ReleaseState.Published;
            if (database.Entry(row).State == EntityState.Detached) database.Releases.Add(row);
        }
        else if (type == "channel-pointer")
        {
            var envelope = SignedDocument.DeserializeEnvelope(bytes);
            var pointer = SignedDocument.DeserializePayload<ChannelPointer>(Base64Url.Decode(envelope.Payload));
            var row = await database.Channels.FindAsync([DatabaseRepositoryId, product, name], cancellationToken).ConfigureAwait(false) ?? new ChannelRow { RepositoryId = DatabaseRepositoryId, ProductId = product, Channel = name, ReleaseId = string.Empty, PointerDigest = string.Empty };
            row.RepositoryId = DatabaseRepositoryId;
            row.ChannelSequence = pointer.ChannelSequence; row.ReleaseId = pointer.ReleaseId; row.PointerDigest = ContentHash.Compute(bytes).ToString();
            if (database.Entry(row).State == EntityState.Detached) database.Channels.Add(row);
        }
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistTrustedKeysAsync(CancellationToken cancellationToken = default)
    {
        if (_databaseFactory is null) return;
        await using var database = await _databaseFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (keyId, publicKey) in TrustedKeys)
        {
            var row = await database.TrustedKeys.FindAsync([DatabaseRepositoryId, keyId], cancellationToken).ConfigureAwait(false) ?? new TrustedKeyRow { RepositoryId = DatabaseRepositoryId, KeyId = keyId, PublicKey = string.Empty };
            row.RepositoryId = DatabaseRepositoryId; row.KeyId = keyId; row.PublicKey = Base64Url.Encode(publicKey); row.UpdatedAt = DateTimeOffset.UtcNow;
            if (database.Entry(row).State == EntityState.Detached) database.TrustedKeys.Add(row);
        }
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertSessionAsync(SessionState session, CancellationToken cancellationToken = default)
    {
        if (_databaseFactory is null) return;
        await using var database = await _databaseFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await database.PublishSessions.FindAsync([session.Id], cancellationToken).ConfigureAwait(false) ?? new PublishSessionRow { SessionId = session.Id, RepositoryId = session.Repository };
        row.RepositoryId = session.Repository; row.ExpiresAt = session.ExpiresAt; row.MaxObjects = session.MaxObjects; row.MaxTotalBytes = session.MaxTotalBytes; row.ObjectCount = session.ObjectCount; row.TotalBytes = session.TotalBytes; row.Sealed = session.Sealed;
        if (database.Entry(row).State == EntityState.Detached) database.PublishSessions.Add(row);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertGrantAsync(GrantState grant, CancellationToken cancellationToken = default)
    {
        if (_databaseFactory is null) return;
        await using var database = await _databaseFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await database.PublishGrants.FindAsync([grant.Id], cancellationToken).ConfigureAwait(false) ?? new PublishGrantRow { GrantId = grant.Id, SessionId = grant.SessionId, RepositoryId = grant.Repository, StagingKey = grant.StagingKey.Value, Digest = grant.Digest.ToString() };
        row.SessionId = grant.SessionId; row.RepositoryId = grant.Repository; row.StagingKey = grant.StagingKey.Value; row.Digest = grant.Digest.ToString(); row.Length = grant.Length; row.ExpiresAt = grant.ExpiresAt; row.Used = grant.Used;
        if (database.Entry(row).State == EntityState.Detached) database.PublishGrants.Add(row);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertVerifiedBlobAsync(string repository, ContentHash digest, DateTimeOffset verifiedAt, CancellationToken cancellationToken = default)
    {
        if (_databaseFactory is null) return;
        await using var database = await _databaseFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await database.BlobPlacements.FindAsync([repository, digest.Algorithm.ToString().ToLowerInvariant(), digest.ToString().Split(':')[1], "configured", _layout.Blob(digest).Value], cancellationToken).ConfigureAwait(false)
                  ?? new BlobPlacementRow { RepositoryId = repository, Algorithm = digest.Algorithm.ToString().ToLowerInvariant(), Hash = digest.ToString().Split(':')[1], BackendId = "configured", ObjectKey = _layout.Blob(digest).Value };
        row.VerifiedAt = verifiedAt;
        if (database.Entry(row).State == EntityState.Detached) database.BlobPlacements.Add(row);
        var reference = await database.BlobRefs.FindAsync(["sha256", digest.ToString().Split(':')[1]], cancellationToken).ConfigureAwait(false);
        if (reference is null)
        {
            var head = _store is null ? null : await _store.HeadAsync(_layout.Blob(digest), cancellationToken).ConfigureAwait(false);
            database.BlobRefs.Add(new BlobRefRow { Algorithm = "sha256", Hash = digest.ToString().Split(':')[1], Length = head?.Length ?? 0, FirstSeenAt = verifiedAt });
        }
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertAuditAsync(AuditEvent item)
    {
        if (_databaseFactory is null) return;
        await using var database = await _databaseFactory.CreateDbContextAsync().ConfigureAwait(false);
        database.AuditEvents.Add(new AuditEventRow { At = item.At, Actor = item.Actor, Action = item.Action, Resource = item.Resource, Outcome = item.Outcome });
        await database.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task UpsertRevocationAsync(string product, byte[] bytes, CancellationToken cancellationToken = default)
    {
        if (_databaseFactory is null) return;
        await using var database = await _databaseFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await database.Revocations.FindAsync([DatabaseRepositoryId, product], cancellationToken).ConfigureAwait(false) ?? new RevocationRow { RepositoryId = DatabaseRepositoryId, ProductId = product, EnvelopeJson = string.Empty };
        row.EnvelopeJson = Convert.ToBase64String(bytes); row.UpdatedAt = DateTimeOffset.UtcNow;
        if (database.Entry(row).State == EntityState.Detached) database.Revocations.Add(row);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
