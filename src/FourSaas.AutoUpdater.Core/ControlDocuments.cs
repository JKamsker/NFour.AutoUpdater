namespace FourSaas.AutoUpdater.Core;

public sealed record ChannelPointer
{
    public int SchemaVersion { get; init; } = 1;
    public required string ProductId { get; init; }
    public required string Channel { get; init; }
    public required long ChannelSequence { get; init; }
    public required long SupersedesChannelSequence { get; init; }
    public required string ReleaseId { get; init; }
    public required long ReleaseSequence { get; init; }
    public required ContentHash ReleaseDigest { get; init; }
    public string? Reason { get; init; }
    public string? MinimumClientVersion { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public enum RevocationEffect { BlockInstall, BlockRepair, ForceMove }
public sealed record RevocationEntry
{
    public required string ReleaseId { get; init; }
    public required string Action { get; init; }
    public required RevocationEffect Effect { get; init; }
    public required string Reason { get; init; }
    public required DateTimeOffset At { get; init; }
}
public sealed record RevocationDocument
{
    public int SchemaVersion { get; init; } = 1;
    public required string ProductId { get; init; }
    public required long RevocationSequence { get; init; }
    public ImmutableArray<RevocationEntry> Entries { get; init; } = [];
}

public sealed record PublicKeyRecord
{
    public required string KeyId { get; init; }
    public required string Algorithm { get; init; }
    public required string PublicKey { get; init; }
    public required DateTimeOffset NotBefore { get; init; }
    public required DateTimeOffset NotAfter { get; init; }
}
public sealed record KeyManifest
{
    public int SchemaVersion { get; init; } = 1;
    public required long KeySequence { get; init; }
    public ImmutableArray<PublicKeyRecord> Keys { get; init; } = [];
    public ImmutableArray<string> RevokedKeyIds { get; init; } = [];
}

public sealed record ChannelAcceptanceResult(bool Accepted, string? Error, bool IsRollback = false);

public static class ControlDocumentPolicy
{
    public static ChannelAcceptanceResult AcceptChannel(ChannelPointer pointer, string productId, string channel, long lastChannelSequence, DateTimeOffset now, TimeSpan stalenessBound, long? lastReleaseSequence = null)
    {
        if (pointer.SchemaVersion != 1) return new(false, $"Unsupported channel schemaVersion {pointer.SchemaVersion}.");
        if (!string.Equals(pointer.ProductId, productId, StringComparison.Ordinal) || !string.Equals(pointer.Channel, channel, StringComparison.Ordinal)) return new(false, "Channel pointer identity does not match the requested product and channel.");
        if (pointer.ChannelSequence <= lastChannelSequence) return new(false, "Channel sequence is not newer than the installed ledger.");
        if (pointer.SupersedesChannelSequence >= pointer.ChannelSequence) return new(false, "Channel pointer supersedes sequence must be lower than its own sequence.");
        if (pointer.UpdatedAt > now.Add(stalenessBound) || pointer.UpdatedAt < now.Subtract(stalenessBound)) return new(false, "Channel pointer is outside the configured staleness bound.");
        return new(true, null, lastReleaseSequence is { } previous && pointer.ReleaseSequence < previous);
    }

    public static bool CanApplyRevocations(KeyManifest current, KeyManifest candidate, out string? error, DateTimeOffset? now = null)
    {
        error = null;
        if (candidate.SchemaVersion != 1) { error = $"Unsupported key manifest schemaVersion {candidate.SchemaVersion}."; return false; }
        if (candidate.KeySequence <= current.KeySequence) { error = "Key sequence must increase."; return false; }
        var currentTime = now ?? DateTimeOffset.UtcNow;
        var active = candidate.Keys.Where(x => !candidate.RevokedKeyIds.Contains(x.KeyId, StringComparer.Ordinal) && x.NotBefore <= currentTime && x.NotAfter >= currentTime).ToArray();
        if (active.Length == 0) { error = "A key manifest may not leave the trusted set empty."; return false; }
        foreach (var revoked in candidate.RevokedKeyIds)
            if (!candidate.Keys.Any(x => x.KeyId != revoked && !candidate.RevokedKeyIds.Contains(x.KeyId, StringComparer.Ordinal) && x.NotBefore <= currentTime && x.NotAfter >= currentTime)) { error = $"Revoked key '{revoked}' has no valid replacement."; return false; }
        return true;
    }

    public static bool ValidateKeyManifest(KeyManifest candidate, IReadOnlySet<string> alreadyTrustedKeyIds, string? pinnedRootKeyId, DateTimeOffset now, TimeSpan clockSkew, out string? error)
    {
        error = null;
        if (candidate.SchemaVersion != 1) { error = $"Unsupported key manifest schemaVersion {candidate.SchemaVersion}."; return false; }
        if (candidate.KeySequence < 1) { error = "Key sequence must be positive."; return false; }
        if (candidate.Keys.IsDefaultOrEmpty) { error = "A key manifest must contain at least one key."; return false; }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in candidate.Keys)
        {
            if (!Identifier.IsValid(key.KeyId, "keyId", out error) || !ids.Add(key.KeyId)) { error ??= $"Duplicate key id '{key.KeyId}'."; return false; }
            if (!string.Equals(key.Algorithm, "ed25519", StringComparison.OrdinalIgnoreCase)) { error = $"Unsupported key algorithm '{key.Algorithm}'."; return false; }
            byte[] publicKey;
            try { publicKey = Base64Url.Decode(key.PublicKey); }
            catch (FormatException ex) { error = $"Key '{key.KeyId}' public key is invalid: {ex.Message}"; return false; }
            if (publicKey.Length != 32) { error = $"Key '{key.KeyId}' is not a 32-byte Ed25519 public key."; return false; }
            if (key.NotAfter <= key.NotBefore) { error = $"Key '{key.KeyId}' has an invalid validity window."; return false; }
            if (key.NotAfter < now - clockSkew || key.NotBefore > now + clockSkew) continue;
        }
        if (pinnedRootKeyId is not null && candidate.RevokedKeyIds.Contains(pinnedRootKeyId, StringComparer.Ordinal)) { error = "The compiled pinned root may not be remotely revoked."; return false; }
        if (candidate.RevokedKeyIds.Any(x => !ids.Contains(x))) { error = "The key manifest revokes an unknown key."; return false; }
        var active = candidate.Keys.Any(x => !candidate.RevokedKeyIds.Contains(x.KeyId, StringComparer.Ordinal) && x.NotBefore <= now + clockSkew && x.NotAfter >= now - clockSkew);
        if (!active) { error = "The key manifest would leave no currently valid trusted key."; return false; }
        if (alreadyTrustedKeyIds.Count > 0 && !candidate.Keys.Any(x => alreadyTrustedKeyIds.Contains(x.KeyId) && !candidate.RevokedKeyIds.Contains(x.KeyId)))
        { error = "The key manifest is not chained from an already trusted key."; return false; }
        return true;
    }
}

public sealed record Ed25519KeyPair(byte[] PrivateKey, byte[] PublicKey)
{
    public static Ed25519KeyPair Create()
    {
        var privateKey = new Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(new Org.BouncyCastle.Security.SecureRandom());
        return new Ed25519KeyPair(privateKey.GetEncoded(), privateKey.GeneratePublicKey().GetEncoded());
    }
}
