namespace NFour.AutoUpdater.Repository;

public sealed record VerifiedChannel(ChannelPointer Pointer, SignedEnvelope Envelope, byte[] EnvelopeBytes);
public sealed record VerifiedRelease(ReleaseLock Lock, SignedEnvelope Envelope, byte[] EnvelopeBytes, ReleaseBundle? Bundle, ChannelPointer? Pointer = null, SignedEnvelope? PointerEnvelope = null);
public sealed record VerifiedKeyManifest(KeyManifest Manifest, SignedEnvelope Envelope, byte[] EnvelopeBytes);
public sealed record VerifiedRevocations(RevocationDocument Document, SignedEnvelope Envelope, byte[] EnvelopeBytes);

public sealed class RepositoryReader(IReadableObjectStore store, RepositoryDescriptor descriptor)
{
    private readonly RepositoryLayout _layout = new(descriptor.Layout);
    public RepositoryDescriptor Descriptor { get; } = descriptor;

    public async ValueTask<VerifiedChannel> ReadChannelAsync(string productId, string channel, IReadOnlyDictionary<string, byte[]> trustedKeys, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadRequiredAsync(_layout.Channel(productId, channel), cancellationToken).ConfigureAwait(false);
        var envelope = SignedDocument.DeserializeEnvelope(bytes);
        if (!SignedDocument.Verify(envelope, trustedKeys, out var payload, out var error)) throw new CryptographicException(error);
        if (!string.Equals(envelope.Type, "channel-pointer", StringComparison.Ordinal)) throw new InvalidDataException("Object is not a channel-pointer envelope.");
        var pointer = SignedDocument.DeserializePayload<ChannelPointer>(payload);
        EnsureDescriptorKeyWindow(envelope, trustedKeys, pointer.UpdatedAt);
        if (pointer.SchemaVersion != 1) throw new FormatException($"Unsupported channel schemaVersion {pointer.SchemaVersion}; minimumClientVersion={Descriptor.MinimumClientVersion ?? "unknown"}.");
        if (pointer.ChannelSequence < 1 || pointer.SupersedesChannelSequence < 0 || pointer.ReleaseSequence < 1) throw new FormatException("Channel sequence fields must be positive, with a non-negative superseded sequence.");
        if (!Identifier.IsValid(pointer.ReleaseId, "releaseId", out var pointerReleaseIdError)) throw new InvalidDataException(pointerReleaseIdError);
        if (!string.Equals(pointer.ProductId, productId, StringComparison.Ordinal) || !string.Equals(pointer.Channel, channel, StringComparison.Ordinal)) throw new InvalidDataException("Channel pointer identity does not match its repository path.");
        return new VerifiedChannel(pointer, envelope, bytes);
    }

    public ValueTask<VerifiedRelease> ReadChannelReleaseAsync(string productId, string channel, IReadOnlyDictionary<string, byte[]> trustedKeys, CancellationToken cancellationToken = default)
        => ReadChannelReleaseCoreAsync(productId, channel, trustedKeys, cancellationToken);

    public ValueTask<VerifiedKeyManifest> ReadKeyManifestAsync(IReadOnlyDictionary<string, byte[]> trustedKeys, long? previousSequence, string? pinnedRootKeyId, DateTimeOffset? now, CancellationToken cancellationToken = default)
        => ReadKeyManifestAsync(trustedKeys, previousSequence, null, pinnedRootKeyId, now, cancellationToken);

    public async ValueTask<VerifiedKeyManifest> ReadKeyManifestAsync(IReadOnlyDictionary<string, byte[]> trustedKeys, long? previousSequence = null, ContentHash? previousEnvelopeDigest = null, string? pinnedRootKeyId = null, DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadRequiredAsync(_layout.KeyManifest(), cancellationToken).ConfigureAwait(false);
        var envelope = SignedDocument.DeserializeEnvelope(bytes);
        if (!string.Equals(envelope.Type, "key-manifest", StringComparison.Ordinal)) throw new InvalidDataException("Object is not a key-manifest envelope.");
        if (!SignedDocument.VerifyCryptographically(envelope, trustedKeys.ToDictionary(x => x.Key, x => new VerificationKey(x.Value), StringComparer.Ordinal), out var payload, out var signer, out var error)) throw new CryptographicException(error);
        var manifest = SignedDocument.DeserializePayload<KeyManifest>(payload);
        var documentTime = now ?? DateTimeOffset.UtcNow;
        var signerRecord = manifest.Keys.FirstOrDefault(x => string.Equals(x.KeyId, signer, StringComparison.Ordinal));
        if (signerRecord is null || manifest.RevokedKeyIds.Contains(signer!, StringComparer.Ordinal)) throw new CryptographicException("The key-manifest signer is not an active member of the candidate manifest.");
        var signerKey = new VerificationKey(Base64Url.Decode(signerRecord.PublicKey), signerRecord.NotBefore, signerRecord.NotAfter);
        var validitySkew = TimeSpan.FromHours(24);
        if (signerKey.PublicKey.Length != 32
            || signerRecord.NotBefore > documentTime + validitySkew
            || signerRecord.NotAfter < documentTime - validitySkew)
            throw new CryptographicException($"Key-manifest signer '{signer}' is outside its validity window.");
        if (previousSequence is { } previous)
        {
            if (manifest.KeySequence < previous) throw new InvalidDataException("Key manifest sequence is older than the accepted manifest.");
            if (manifest.KeySequence == previous && (previousEnvelopeDigest is null || ContentHash.Compute(bytes) != previousEnvelopeDigest.Value))
                throw new InvalidDataException("Key manifest sequence must strictly increase unless the envelope is byte-identical to the accepted manifest.");
        }
        if (!ControlDocumentPolicy.ValidateKeyManifest(manifest, trustedKeys, pinnedRootKeyId, now ?? DateTimeOffset.UtcNow, TimeSpan.FromHours(24), out var policyError)) throw new InvalidDataException(policyError);
        return new VerifiedKeyManifest(manifest, envelope, bytes);
    }

    public async ValueTask<VerifiedRevocations?> ReadRevocationsAsync(string productId, IReadOnlyDictionary<string, byte[]> trustedKeys, long? previousSequence = null, ContentHash? previousEnvelopeDigest = null, CancellationToken cancellationToken = default, DateTimeOffset? now = null, TimeSpan? stalenessBound = null)
    {
        var result = await store.OpenAsync(_layout.Revocations(productId), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result is null) return null;
        await using (result.ConfigureAwait(false))
        {
            var bytes = await ReadAllAsync(result.Content, cancellationToken).ConfigureAwait(false);
            var envelope = SignedDocument.DeserializeEnvelope(bytes);
            if (!string.Equals(envelope.Type, "revocation", StringComparison.Ordinal)) throw new InvalidDataException("Object is not a revocation envelope.");
            if (!SignedDocument.Verify(envelope, trustedKeys, out var payload, out var error)) throw new CryptographicException(error);
            var document = SignedDocument.DeserializePayload<RevocationDocument>(payload);
            var currentTime = now ?? DateTimeOffset.UtcNow;
            var documentTime = document.UpdatedAt;
            EnsureDescriptorKeyWindow(envelope, trustedKeys, documentTime);
            if (document.SchemaVersion != 1 || !string.Equals(document.ProductId, productId, StringComparison.Ordinal)) throw new InvalidDataException("Revocation document identity does not match its repository path.");
            if (document.RevocationSequence < 1) throw new FormatException("Revocation sequence must be positive.");
            if (document.Entries.Any(x => !string.Equals(x.Action, "yank", StringComparison.Ordinal))) throw new InvalidDataException("Revocation documents may contain only yank actions.");
            foreach (var entry in document.Entries)
                if (!Identifier.IsValid(entry.ReleaseId, "releaseId", out var entryReleaseIdError)) throw new InvalidDataException(entryReleaseIdError);
            if (previousSequence is { } previous)
            {
                if (document.RevocationSequence < previous) throw new InvalidDataException("Revocation sequence is older than the accepted document.");
                if (document.RevocationSequence == previous && (previousEnvelopeDigest is null || ContentHash.Compute(bytes) != previousEnvelopeDigest.Value))
                    throw new InvalidDataException("Revocation sequence must strictly increase unless the envelope is byte-identical to the accepted document.");
            }
            var freshness = stalenessBound ?? TimeSpan.FromDays(7);
            if (documentTime < currentTime.Subtract(freshness) || documentTime > currentTime.Add(freshness)) throw new InvalidDataException("Revocation document is outside the configured freshness bound.");
            return new VerifiedRevocations(document, envelope, bytes);
        }
    }

    public ValueTask<VerifiedRevocations?> ReadRevocationsAsync(string productId, IReadOnlyDictionary<string, byte[]> trustedKeys, long? previousSequence, CancellationToken cancellationToken, DateTimeOffset? now = null, TimeSpan? stalenessBound = null)
        => ReadRevocationsAsync(productId, trustedKeys, previousSequence, null, cancellationToken, now, stalenessBound);

    public ValueTask<VerifiedRelease> ReadReleaseAsync(string productId, string releaseId, IReadOnlyDictionary<string, byte[]> trustedKeys, CancellationToken cancellationToken)
        => ReadReleaseAsync(productId, releaseId, trustedKeys, null, cancellationToken);

    public async ValueTask<VerifiedRelease> ReadReleaseAsync(string productId, string releaseId, IReadOnlyDictionary<string, byte[]> trustedKeys, ContentHash? expectedEnvelopeDigest = null, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadRequiredAsync(_layout.Release(productId, releaseId), cancellationToken).ConfigureAwait(false);
        if (expectedEnvelopeDigest is { } expected && ContentHash.Compute(bytes) != expected) throw new CryptographicException("Release envelope digest does not match the channel pointer.");
        var envelope = SignedDocument.DeserializeEnvelope(bytes);
        if (!SignedDocument.Verify(envelope, trustedKeys, out var payload, out var error)) throw new CryptographicException(error);
        if (!string.Equals(envelope.Type, "release-lock", StringComparison.Ordinal)) throw new InvalidDataException("Object is not a release-lock envelope.");
        var release = SignedDocument.DeserializePayload<ReleaseLock>(payload);
        EnsureDescriptorKeyWindow(envelope, trustedKeys, release.CreatedAt);
        if (release.SchemaVersion != 1) throw new FormatException($"Unsupported release schemaVersion {release.SchemaVersion}; minimumClientVersion={Descriptor.MinimumClientVersion ?? "unknown"}.");
        if (release.Sequence < 1) throw new FormatException("Release sequence must be positive.");
        if (!Identifier.IsValid(release.ReleaseId, "releaseId", out var releaseIdError)) throw new InvalidDataException(releaseIdError);
        if (!string.Equals(release.ProductId, productId, StringComparison.Ordinal) || !string.Equals(release.ReleaseId, releaseId, StringComparison.Ordinal)) throw new InvalidDataException("Release identity does not match its repository path.");
        if (release.State != ReleaseState.Published) throw new InvalidDataException($"Release '{releaseId}' is not published (state={release.State}).");
        ReleaseBundle? bundle = null;
        var bundleKey = _layout.ReleaseBundle(productId, releaseId);
        var bundleResult = await store.OpenAsync(bundleKey, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (bundleResult is not null)
        {
            await using (bundleResult.ConfigureAwait(false))
            {
                var bundleBytes = await ReadAllAsync(bundleResult.Content, cancellationToken).ConfigureAwait(false);
                try
                {
                    var bundleDocument = RepositoryJson.Deserialize<BundleDocument>(bundleBytes, rejectUnknownFields: true);
                    if (!string.Equals(bundleDocument.LockEnvelope, Base64Url.Encode(bytes), StringComparison.Ordinal)) throw new InvalidDataException("Bundle lock envelope differs from the signed release.");
                    if (bundleDocument.SchemaVersion != 1) throw new FormatException($"Unsupported release bundle schemaVersion {bundleDocument.SchemaVersion}.");
                    if (bundleDocument is not null)
                    {
                        var inline = ImmutableDictionary.CreateBuilder<PackageId, PackageManifest>();
                        foreach (var item in bundleDocument.Inline)
                        {
                            if (!PackageId.TryCreate(item.Key, out var inlineId)) throw new FormatException($"Bundle contains an invalid inline package id '{item.Key}'.");
                            // Exact pinned bytes, base64url-encoded by the projection writer.  These
                            // are hashed as-is; nothing may re-serialize them before the digest check.
                            byte[] manifestBytes;
                            try { manifestBytes = Base64Url.Decode(item.Value); }
                            catch (FormatException) { throw new InvalidDataException($"Bundle inline manifest '{item.Key}' is not valid base64url."); }
                            if (manifestBytes.Length > MaximumInlineManifestBytes) throw new InvalidDataException($"Bundle inline manifest '{item.Key}' exceeds the {MaximumInlineManifestBytes} byte limit.");
                            var pin = release.Packages.FirstOrDefault(x => x.Id.Value == item.Key) ?? throw new InvalidDataException($"Bundle contains unpinned manifest '{item.Key}'.");
                            if (ContentHash.Compute(manifestBytes) != pin.ManifestDigest) throw new CryptographicException($"Inline manifest '{item.Key}' failed its pinned digest.");
                            var storedManifestBytes = await ReadRequiredAsync(_layout.Package(pin.Id, pin.Version), cancellationToken).ConfigureAwait(false);
                            if (ContentHash.Compute(storedManifestBytes) != pin.ManifestDigest) throw new CryptographicException($"Stored manifest '{item.Key}' failed its release pin.");
                            var manifest = RepositoryJson.DeserializeManifest(manifestBytes);
                            if (manifest.SchemaVersion != 1) throw new FormatException($"Unsupported inline package schemaVersion {manifest.SchemaVersion}.");
                            if (manifest.Id != inlineId || manifest.Id != pin.Id || !string.Equals(manifest.Version.Label, pin.Version.Label, StringComparison.Ordinal) || manifest.Sequence != pin.Sequence)
                                throw new InvalidDataException($"Inline manifest '{item.Key}' identity does not match its release pin.");
                            inline[inlineId] = manifest;
                        }
                        if (release.Packages.Any(pin => !inline.ContainsKey(pin.Id))) throw new InvalidDataException("Release bundle is missing an inline manifest for a pinned package.");
                        bundle = new ReleaseBundle { Lock = release, LockEnvelope = bundleDocument.LockEnvelope, Inline = inline.ToImmutable() };
                    }
                }
                catch (JsonException) { throw new InvalidDataException("Release bundle is invalid."); }
            }
        }
        return new VerifiedRelease(release, envelope, bytes, bundle);
    }

    private async ValueTask<VerifiedRelease> ReadChannelReleaseCoreAsync(string productId, string channel, IReadOnlyDictionary<string, byte[]> trustedKeys, CancellationToken cancellationToken)
    {
        var verifiedChannel = await ReadChannelAsync(productId, channel, trustedKeys, cancellationToken).ConfigureAwait(false);
        var release = await ReadReleaseAsync(productId, verifiedChannel.Pointer.ReleaseId, trustedKeys, verifiedChannel.Pointer.ReleaseDigest, cancellationToken).ConfigureAwait(false);
        if (release.Lock.Sequence != verifiedChannel.Pointer.ReleaseSequence) throw new InvalidDataException("Channel pointer releaseSequence does not match the release lock.");
        return release with { Pointer = verifiedChannel.Pointer, PointerEnvelope = verifiedChannel.Envelope };
    }

    private async ValueTask<byte[]> ReadRequiredAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        var result = await store.OpenAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException(key.Value);
        await using (result.ConfigureAwait(false)) return await ReadAllAsync(result.Content, cancellationToken).ConfigureAwait(false);
    }
    private static async ValueTask<byte[]> ReadAllAsync(Stream source, CancellationToken cancellationToken) { using var target = new MemoryStream(); await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false); return target.ToArray(); }
    private void EnsureDescriptorKeyWindow(SignedEnvelope envelope, IReadOnlyDictionary<string, byte[]> trustedKeys, DateTimeOffset documentTime)
    {
        var descriptorKeys = Descriptor.TrustedKeys
            .Where(x => string.Equals(x.Algorithm, "ed25519", StringComparison.Ordinal) && trustedKeys.TryGetValue(x.KeyId, out var supplied) && Base64Url.Encode(supplied) == x.PublicKey)
            .ToDictionary(x => x.KeyId, x => new VerificationKey(Base64Url.Decode(x.PublicKey), x.NotBefore, x.NotAfter), StringComparer.Ordinal);
        if (descriptorKeys.Count == 0) return;
        var skew = TimeSpan.FromHours(24);
        if (!SignedDocument.VerifyCryptographically(envelope, descriptorKeys, out _, out _, out var error, signer => SignedDocument.IsKeyValidAt(descriptorKeys, signer, documentTime, out _, skew)))
            throw new CryptographicException(error);
    }
    private const int MaximumInlineManifestBytes = 4 * 1024 * 1024;
    private sealed record BundleDocument { public required int SchemaVersion { get; init; } public required string LockEnvelope { get; init; } public required Dictionary<string, string> Inline { get; init; } }
}
