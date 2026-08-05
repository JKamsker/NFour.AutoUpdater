namespace FourSaas.AutoUpdater.Repository;

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
        if (pointer.SchemaVersion != 1) throw new FormatException($"Unsupported channel schemaVersion {pointer.SchemaVersion}.");
        if (!string.Equals(pointer.ProductId, productId, StringComparison.Ordinal) || !string.Equals(pointer.Channel, channel, StringComparison.Ordinal)) throw new InvalidDataException("Channel pointer identity does not match its repository path.");
        return new VerifiedChannel(pointer, envelope, bytes);
    }

    public ValueTask<VerifiedRelease> ReadChannelReleaseAsync(string productId, string channel, IReadOnlyDictionary<string, byte[]> trustedKeys, CancellationToken cancellationToken = default)
        => ReadChannelReleaseCoreAsync(productId, channel, trustedKeys, cancellationToken);

    public async ValueTask<VerifiedKeyManifest> ReadKeyManifestAsync(IReadOnlyDictionary<string, byte[]> trustedKeys, long? previousSequence = null, string? pinnedRootKeyId = null, DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadRequiredAsync(_layout.KeyManifest(), cancellationToken).ConfigureAwait(false);
        var envelope = SignedDocument.DeserializeEnvelope(bytes);
        if (!string.Equals(envelope.Type, "key-manifest", StringComparison.Ordinal)) throw new InvalidDataException("Object is not a key-manifest envelope.");
        if (!SignedDocument.Verify(envelope, trustedKeys, out var payload, out var error)) throw new CryptographicException(error);
        var manifest = SignedDocument.DeserializePayload<KeyManifest>(payload);
        EnsureDescriptorKeyWindow(envelope, trustedKeys, now ?? DateTimeOffset.UtcNow);
        // Re-reading the same immutable control object is normal on every update.  A
        // strictly lower sequence is replay; equality is the idempotent unchanged case.
        if (previousSequence is { } previous && manifest.KeySequence < previous) throw new InvalidDataException("Key manifest sequence is older than the accepted manifest.");
        if (!ControlDocumentPolicy.ValidateKeyManifest(manifest, trustedKeys.Keys.ToHashSet(StringComparer.Ordinal), pinnedRootKeyId, now ?? DateTimeOffset.UtcNow, TimeSpan.FromHours(24), out var policyError)) throw new InvalidDataException(policyError);
        return new VerifiedKeyManifest(manifest, envelope, bytes);
    }

    public async ValueTask<VerifiedRevocations?> ReadRevocationsAsync(string productId, IReadOnlyDictionary<string, byte[]> trustedKeys, long? previousSequence = null, CancellationToken cancellationToken = default, DateTimeOffset? now = null, TimeSpan? stalenessBound = null)
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
            EnsureDescriptorKeyWindow(envelope, trustedKeys, document.Entries.IsDefaultOrEmpty ? now ?? DateTimeOffset.UtcNow : document.Entries.Max(x => x.At));
            if (document.SchemaVersion != 1 || !string.Equals(document.ProductId, productId, StringComparison.Ordinal)) throw new InvalidDataException("Revocation document identity does not match its repository path.");
            if (previousSequence is { } previous && document.RevocationSequence < previous) throw new InvalidDataException("Revocation sequence is older than the accepted document.");
            if (!document.Entries.IsDefaultOrEmpty && document.Entries.Max(x => x.At) < (now ?? DateTimeOffset.UtcNow).Subtract(stalenessBound ?? TimeSpan.FromDays(7))) throw new InvalidDataException("Revocation document is stale.");
            return new VerifiedRevocations(document, envelope, bytes);
        }
    }

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
        if (release.SchemaVersion != 1) throw new FormatException($"Unsupported release schemaVersion {release.SchemaVersion}.");
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
                            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(item.Value, RepositoryJson.Options);
                            var pin = release.Packages.FirstOrDefault(x => x.Id.Value == item.Key) ?? throw new InvalidDataException($"Bundle contains unpinned manifest '{item.Key}'.");
                            if (ContentHash.Compute(manifestBytes) != pin.ManifestDigest) throw new CryptographicException($"Inline manifest '{item.Key}' failed its pinned digest.");
                            var manifest = RepositoryJson.DeserializeManifest(manifestBytes);
                            if (manifest.SchemaVersion != 1) throw new FormatException($"Unsupported inline package schemaVersion {manifest.SchemaVersion}.");
                            if (manifest.Id != inlineId || manifest.Id != pin.Id || !string.Equals(manifest.Version.Label, pin.Version.Label, StringComparison.Ordinal) || manifest.Version.Sequence != pin.Version.Sequence)
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
            .Where(x => string.Equals(x.Algorithm, "ed25519", StringComparison.OrdinalIgnoreCase) && trustedKeys.TryGetValue(x.KeyId, out var supplied) && Base64Url.Encode(supplied) == x.PublicKey)
            .ToDictionary(x => x.KeyId, x => new VerificationKey(Base64Url.Decode(x.PublicKey), x.NotBefore, x.NotAfter), StringComparer.Ordinal);
        if (descriptorKeys.Count == 0) return;
        var skew = TimeSpan.FromHours(24);
        if (!SignedDocument.VerifyCryptographically(envelope, descriptorKeys, out _, out _, out var error, signer => SignedDocument.IsKeyValidAt(descriptorKeys, signer, documentTime, out _, skew)))
            throw new CryptographicException(error);
    }
    private sealed record BundleDocument { public required int SchemaVersion { get; init; } public required string LockEnvelope { get; init; } public required Dictionary<string, JsonElement> Inline { get; init; } }
}
