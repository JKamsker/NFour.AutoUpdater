using System.Collections.Concurrent;
using System.Text.Json;
using FourSaas.AutoUpdater.Core;
using FourSaas.AutoUpdater.Storage;

namespace FourSaas.AutoUpdater.Server;

public sealed class ManagementState
{
    private sealed record SessionState(string Id, DateTimeOffset ExpiresAt, int MaxObjects, long MaxTotalBytes, int ObjectCount, long TotalBytes, bool Sealed);
    private sealed record GrantState(string Id, string SessionId, ObjectKey StagingKey, ContentHash Digest, long Length, DateTimeOffset ExpiresAt, bool Used);
    public string RepositoryBaseUrl { get; init; } = "";
    public ConcurrentDictionary<string, object> Products { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<(string Product, string Channel), byte[]> Channels { get; } = new();
    public ConcurrentDictionary<(string Product, string Release), byte[]> Releases { get; } = new();
    public ConcurrentDictionary<string, object> Sessions { get; } = new(StringComparer.Ordinal);
    private ConcurrentDictionary<string, SessionState> PublishSessions { get; } = new(StringComparer.Ordinal);
    private ConcurrentDictionary<string, GrantState> Grants { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, DateTimeOffset> VerifiedBlobs { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, byte[]> TrustedKeys { get; } = new(StringComparer.Ordinal);
    public object OpenSession()
    {
        var id = Guid.NewGuid().ToString("N");
        var expires = DateTimeOffset.UtcNow.AddMinutes(15);
        PublishSessions[id] = new SessionState(id, expires, 100_000, 1_000_000_000_000L, 0, 0, false);
        Sessions[id] = new();
        return new { sessionId = id, maxObjects = 100_000, maxTotalBytes = 1_000_000_000_000L, expiresAt = expires };
    }

    public string[] QueryBlobs(IEnumerable<string> requested) => requested.Where(x => ContentHash.TryParse(x, out var hash) && hash.Algorithm == HashAlgorithmId.Sha256 && VerifiedBlobs.ContainsKey(hash.ToString())).ToArray();

    public async Task<IResult> CreateGrantsAsync(string sessionId, HttpRequest request)
    {
        if (!PublishSessions.TryGetValue(sessionId, out var session) || session.Sealed || session.ExpiresAt <= DateTimeOffset.UtcNow) return Results.NotFound();
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
            Grants[grantId] = new GrantState(grantId, sessionId, stagingKey, item.Hash, item.Length, expires, false);
            response.Add(new { grantId, stagingKey = stagingKey.Value, expectedDigest = item.Hash.ToString(), expectedLength = item.Length, expiresAt = expires, enforcement = "serverVerified" });
        }
        PublishSessions[sessionId] = session with { ObjectCount = session.ObjectCount + requested.Count, TotalBytes = session.TotalBytes + requested.Sum(x => x.Length) };
        return Results.Ok(new { items = response });
    }

    public IResult SealSession(string sessionId)
    {
        if (!PublishSessions.TryGetValue(sessionId, out var session)) return Results.NotFound();
        if (!PublishSessions.TryUpdate(sessionId, session with { Sealed = true }, session)) return Results.Conflict();
        return Results.Ok(new { sealedSession = true, verificationRequired = true });
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
            if (expectedType == "channel-pointer") _ = SignedDocument.DeserializePayload<ChannelPointer>(payload);
            else _ = SignedDocument.DeserializePayload<ReleaseLock>(payload);
        }
        catch (Exception ex) { return Results.BadRequest(new { error = "payload_invalid", detail = ex.Message }); }
        if (target.TryGetValue((product, name), out var previous))
        {
            if (expectedType == "release-lock") return previous.AsSpan().SequenceEqual(bytes) ? Results.NoContent() : Results.Conflict();
            try
            {
                var oldEnvelope = SignedDocument.DeserializeEnvelope(previous); SignedDocument.Verify(oldEnvelope, TrustedKeys, out var oldPayload, out _);
                var oldPointer = SignedDocument.DeserializePayload<ChannelPointer>(oldPayload); var newPointer = SignedDocument.DeserializePayload<ChannelPointer>(payload);
                if (newPointer.ChannelSequence <= oldPointer.ChannelSequence) return Results.Conflict();
            }
            catch (Exception) { return Results.Conflict(); }
        }
        // The server stores the verified envelope byte-for-byte. It has no signing primitive and never
        // constructs a release lock or pointer. Channel promotion is compare-and-swap so two
        // operators cannot both validate the same predecessor and silently regress the channel.
        var key = (product, name);
        if (previous is not null)
        {
            if (!target.TryUpdate(key, bytes, previous)) return Results.Conflict();
        }
        else if (!target.TryAdd(key, bytes)) return Results.Conflict();
        return Results.NoContent();
    }
}
