namespace NFour.AutoUpdater.Core;

/// <summary>Points a product channel at an immutable release lock.</summary>
public sealed record ChannelPointer
{
    /// <summary>Gets the document schema version.</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Gets the product identifier.</summary>
    public required string ProductId { get; init; }
    /// <summary>Gets the channel identifier.</summary>
    public required string Channel { get; init; }
    /// <summary>Gets the monotonic channel update sequence.</summary>
    public required long ChannelSequence { get; init; }
    /// <summary>Gets the preceding channel sequence this update supersedes.</summary>
    public required long SupersedesChannelSequence { get; init; }
    /// <summary>Gets the target release identifier.</summary>
    public required string ReleaseId { get; init; }
    /// <summary>Gets the target release sequence.</summary>
    public required long ReleaseSequence { get; init; }
    /// <summary>Gets the digest of the exact target release-lock bytes.</summary>
    public required ContentHash ReleaseDigest { get; init; }
    /// <summary>Gets the optional reason for the channel movement.</summary>
    public string? Reason { get; init; }
    /// <summary>Gets the minimum client version allowed to follow the pointer.</summary>
    public string? MinimumClientVersion { get; init; }
    /// <summary>Gets the pointer publication time.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Defines how a revoked release affects client operations.</summary>
public enum RevocationEffect
{
    /// <summary>Prevents new installation of the release.</summary>
    BlockInstall,
    /// <summary>Prevents repair from restoring the release.</summary>
    BlockRepair,
    /// <summary>Requires the client to move away from the release.</summary>
    ForceMove
}
/// <summary>Describes one release revocation action.</summary>
public sealed record RevocationEntry
{
    /// <summary>Gets the affected release identifier.</summary>
    public required string ReleaseId { get; init; }
    /// <summary>Gets the stable action identifier.</summary>
    public required string Action { get; init; }
    /// <summary>Gets the required client behavior.</summary>
    public required RevocationEffect Effect { get; init; }
    /// <summary>Gets the human-readable revocation reason.</summary>
    public required string Reason { get; init; }
    /// <summary>Gets when the revocation took effect.</summary>
    public required DateTimeOffset At { get; init; }
}
/// <summary>Contains the current release revocations for a product.</summary>
public sealed record RevocationDocument
{
    /// <summary>Gets the document schema version.</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Gets the product identifier.</summary>
    public required string ProductId { get; init; }
    /// <summary>Gets the monotonic revocation-document sequence.</summary>
    public required long RevocationSequence { get; init; }
    /// <summary>Gets the document publication time.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
    /// <summary>Gets the release revocation entries.</summary>
    public ImmutableArray<RevocationEntry> Entries { get; init; } = [];
}

/// <summary>Describes a time-bounded public verification key.</summary>
public sealed record PublicKeyRecord
{
    /// <summary>Gets the stable key identifier.</summary>
    public required string KeyId { get; init; }
    /// <summary>Gets the signature algorithm identifier.</summary>
    public required string Algorithm { get; init; }
    /// <summary>Gets the base64url-encoded public-key bytes.</summary>
    public required string PublicKey { get; init; }
    /// <summary>Gets the beginning of the key validity window.</summary>
    public required DateTimeOffset NotBefore { get; init; }
    /// <summary>Gets the end of the key validity window.</summary>
    public required DateTimeOffset NotAfter { get; init; }
}
/// <summary>Contains the trusted verification-key set and its revocations.</summary>
public sealed record KeyManifest
{
    /// <summary>Gets the document schema version.</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Gets the monotonic key-manifest sequence.</summary>
    public required long KeySequence { get; init; }
    /// <summary>Gets the declared verification keys.</summary>
    public ImmutableArray<PublicKeyRecord> Keys { get; init; } = [];
    /// <summary>Gets key identifiers that must no longer be trusted.</summary>
    public ImmutableArray<string> RevokedKeyIds { get; init; } = [];
}

/// <summary>Reports whether a channel pointer is safe to accept.</summary>
/// <param name="Accepted">Whether the pointer passed policy validation.</param>
/// <param name="Error">The rejection reason, if any.</param>
/// <param name="IsRollback">Whether the pointer targets an older release sequence.</param>
public sealed record ChannelAcceptanceResult(bool Accepted, string? Error, bool IsRollback = false);

/// <summary>Applies rollback, freshness, identity, and trust-continuity rules to control documents.</summary>
public static class ControlDocumentPolicy
{
    private const int SupportedSchemaVersion = 1;
    private const int Ed25519PublicKeyLength = 32;
    private const string Ed25519Algorithm = "ed25519";

    /// <summary>Validates whether a channel pointer may advance an installed ledger.</summary>
    /// <param name="pointer">The candidate pointer.</param>
    /// <param name="productId">The expected product identifier.</param>
    /// <param name="channel">The expected channel identifier.</param>
    /// <param name="lastChannelSequence">The last accepted channel sequence.</param>
    /// <param name="now">The trusted evaluation time.</param>
    /// <param name="stalenessBound">The permitted clock and publication-age window.</param>
    /// <param name="lastReleaseSequence">The last accepted release sequence.</param>
    /// <param name="clientVersion">The running client version.</param>
    /// <returns>The acceptance result.</returns>
    public static ChannelAcceptanceResult AcceptChannel(ChannelPointer pointer, string productId, string channel, long lastChannelSequence, DateTimeOffset now, TimeSpan stalenessBound, long? lastReleaseSequence = null, Version? clientVersion = null)
    {
        if (pointer.SchemaVersion != SupportedSchemaVersion) return new(false, $"Unsupported channel schemaVersion {pointer.SchemaVersion}.");
        if (!string.Equals(pointer.ProductId, productId, StringComparison.Ordinal) || !string.Equals(pointer.Channel, channel, StringComparison.Ordinal)) return new(false, "Channel pointer identity does not match the requested product and channel.");
        if (pointer.ChannelSequence < 1 || pointer.ReleaseSequence < 1 || pointer.SupersedesChannelSequence < 0) return new(false, "Channel sequence fields are invalid.");
        if (pointer.ChannelSequence <= lastChannelSequence) return new(false, "Channel sequence is not newer than the installed ledger.");
        if (pointer.SupersedesChannelSequence >= pointer.ChannelSequence) return new(false, "Channel pointer supersedes sequence must be lower than its own sequence.");
        if (pointer.UpdatedAt > now.Add(stalenessBound) || pointer.UpdatedAt < now.Subtract(stalenessBound)) return new(false, "Channel pointer is outside the configured staleness bound.");
        if (pointer.MinimumClientVersion is { } minimumText)
        {
            if (!Version.TryParse(minimumText, out var minimum)) return new(false, $"Channel pointer minimumClientVersion '{minimumText}' is invalid.");
            if (clientVersion is { } current && current < minimum) return new(false, $"Client version {current} is below the channel minimum {minimum}.");
        }
        return new(true, null, lastReleaseSequence is { } previous && pointer.ReleaseSequence < previous);
    }

    /// <summary>Validates that a newer key manifest preserves a usable trust set.</summary>
    /// <param name="current">The currently trusted manifest.</param>
    /// <param name="candidate">The proposed replacement.</param>
    /// <param name="error">Receives the rejection reason.</param>
    /// <param name="now">The evaluation time, or UTC now when omitted.</param>
    /// <returns><see langword="true"/> when the candidate may be applied.</returns>
    public static bool CanApplyRevocations(KeyManifest current, KeyManifest candidate, out string? error, DateTimeOffset? now = null)
    {
        error = null;
        if (candidate.SchemaVersion != SupportedSchemaVersion) { error = $"Unsupported key manifest schemaVersion {candidate.SchemaVersion}."; return false; }
        if (candidate.KeySequence <= current.KeySequence) { error = "Key sequence must increase."; return false; }
        var currentTime = now ?? DateTimeOffset.UtcNow;
        var active = candidate.Keys.Where(x => !candidate.RevokedKeyIds.Contains(x.KeyId, StringComparer.Ordinal) && x.NotBefore <= currentTime && x.NotAfter >= currentTime).ToArray();
        if (active.Length == 0) { error = "A key manifest may not leave the trusted set empty."; return false; }
        foreach (var revoked in candidate.RevokedKeyIds)
            if (!candidate.Keys.Any(x => x.KeyId != revoked && !candidate.RevokedKeyIds.Contains(x.KeyId, StringComparer.Ordinal) && x.NotBefore <= currentTime && x.NotAfter >= currentTime)) { error = $"Revoked key '{revoked}' has no valid replacement."; return false; }
        return true;
    }

    /// <summary>Validates a key manifest against known trusted key identifiers.</summary>
    /// <param name="candidate">The candidate key manifest.</param>
    /// <param name="alreadyTrustedKeyIds">The identifiers already trusted by the client.</param>
    /// <param name="pinnedRootKeyId">The compiled root identifier that must remain present.</param>
    /// <param name="now">The trusted evaluation time.</param>
    /// <param name="clockSkew">The permitted clock skew.</param>
    /// <param name="error">Receives the rejection reason.</param>
    /// <returns><see langword="true"/> when the manifest preserves trust continuity.</returns>
    public static bool ValidateKeyManifest(KeyManifest candidate, IReadOnlySet<string> alreadyTrustedKeyIds, string? pinnedRootKeyId, DateTimeOffset now, TimeSpan clockSkew, out string? error)
        => ValidateKeyManifestCore(candidate, alreadyTrustedKeyIds, null, pinnedRootKeyId, now, clockSkew, out error);

    /// <summary>Validates a key manifest against known key identifiers and immutable key bytes.</summary>
    /// <param name="candidate">The candidate key manifest.</param>
    /// <param name="alreadyTrustedKeys">The keys already trusted by the client.</param>
    /// <param name="pinnedRootKeyId">The compiled root identifier that must remain present.</param>
    /// <param name="now">The trusted evaluation time.</param>
    /// <param name="clockSkew">The permitted clock skew.</param>
    /// <param name="error">Receives the rejection reason.</param>
    /// <returns><see langword="true"/> when the manifest preserves trust continuity and key identity.</returns>
    public static bool ValidateKeyManifest(KeyManifest candidate, IReadOnlyDictionary<string, byte[]> alreadyTrustedKeys, string? pinnedRootKeyId, DateTimeOffset now, TimeSpan clockSkew, out string? error)
        => ValidateKeyManifestCore(candidate, alreadyTrustedKeys.Keys.ToHashSet(StringComparer.Ordinal), alreadyTrustedKeys, pinnedRootKeyId, now, clockSkew, out error);

    private static bool ValidateKeyManifestCore(KeyManifest candidate, IReadOnlySet<string> alreadyTrustedKeyIds, IReadOnlyDictionary<string, byte[]>? alreadyTrustedKeys, string? pinnedRootKeyId, DateTimeOffset now, TimeSpan clockSkew, out string? error)
    {
        error = null;
        if (candidate.SchemaVersion != SupportedSchemaVersion) { error = $"Unsupported key manifest schemaVersion {candidate.SchemaVersion}."; return false; }
        if (candidate.KeySequence < 1) { error = "Key sequence must be positive."; return false; }
        if (candidate.Keys.IsDefaultOrEmpty) { error = "A key manifest must contain at least one key."; return false; }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in candidate.Keys)
        {
            if (!Identifier.IsValid(key.KeyId, "keyId", out error) || !ids.Add(key.KeyId)) { error ??= $"Duplicate key id '{key.KeyId}'."; return false; }
            if (!string.Equals(key.Algorithm, Ed25519Algorithm, StringComparison.Ordinal)) { error = $"Unsupported key algorithm '{key.Algorithm}'."; return false; }
            byte[] publicKey;
            try { publicKey = Base64Url.Decode(key.PublicKey); }
            catch (FormatException ex) { error = $"Key '{key.KeyId}' public key is invalid: {ex.Message}"; return false; }
            if (publicKey.Length != Ed25519PublicKeyLength) { error = $"Key '{key.KeyId}' is not a {Ed25519PublicKeyLength}-byte Ed25519 public key."; return false; }
            if (key.NotAfter <= key.NotBefore) { error = $"Key '{key.KeyId}' has an invalid validity window."; return false; }
            if (key.NotAfter < now - clockSkew || key.NotBefore > now + clockSkew) continue;
        }
        if (pinnedRootKeyId is not null && !ids.Contains(pinnedRootKeyId)) { error = "The compiled pinned root must remain in every key manifest."; return false; }
        if (pinnedRootKeyId is not null && candidate.RevokedKeyIds.Contains(pinnedRootKeyId, StringComparer.Ordinal)) { error = "The compiled pinned root may not be remotely revoked."; return false; }
        if (candidate.RevokedKeyIds.Any(x => !ids.Contains(x))) { error = "The key manifest revokes an unknown key."; return false; }
        if (alreadyTrustedKeys is not null)
        {
            foreach (var (keyId, trustedPublicKey) in alreadyTrustedKeys)
            {
                var candidateKey = candidate.Keys.FirstOrDefault(x => string.Equals(x.KeyId, keyId, StringComparison.Ordinal));
                if (candidateKey is null) { error = $"The key manifest removed already trusted key '{keyId}'."; return false; }
                byte[] candidatePublicKey;
                try { candidatePublicKey = Base64Url.Decode(candidateKey.PublicKey); }
                catch (FormatException) { error = $"Trusted key '{keyId}' has invalid public-key bytes."; return false; }
                if (!CryptographicOperations.FixedTimeEquals(trustedPublicKey, candidatePublicKey)) { error = $"The key manifest changed the public key for already trusted key '{keyId}'."; return false; }
            }
        }
        var active = candidate.Keys.Any(x => !candidate.RevokedKeyIds.Contains(x.KeyId, StringComparer.Ordinal) && x.NotBefore <= now + clockSkew && x.NotAfter >= now - clockSkew);
        if (!active) { error = "The key manifest would leave no currently valid trusted key."; return false; }
        if (alreadyTrustedKeyIds.Count > 0 && !candidate.Keys.Any(x => alreadyTrustedKeyIds.Contains(x.KeyId) && !candidate.RevokedKeyIds.Contains(x.KeyId)))
        { error = "The key manifest is not chained from an already trusted key."; return false; }
        return true;
    }
}

/// <summary>Contains a newly generated Ed25519 signing-key pair.</summary>
/// <param name="PrivateKey">The private signing-key bytes.</param>
/// <param name="PublicKey">The public verification-key bytes.</param>
public sealed record Ed25519KeyPair(byte[] PrivateKey, byte[] PublicKey)
{
    /// <summary>Generates a cryptographically secure Ed25519 key pair.</summary>
    /// <returns>The generated key pair.</returns>
    public static Ed25519KeyPair Create()
    {
        var privateKey = new Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(new Org.BouncyCastle.Security.SecureRandom());
        return new Ed25519KeyPair(privateKey.GetEncoded(), privateKey.GeneratePublicKey().GetEncoded());
    }
}
