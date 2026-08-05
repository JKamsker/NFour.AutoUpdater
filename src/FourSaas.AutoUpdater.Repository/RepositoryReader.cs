namespace FourSaas.AutoUpdater.Repository;

public sealed record VerifiedChannel(ChannelPointer Pointer, SignedEnvelope Envelope, byte[] EnvelopeBytes);
public sealed record VerifiedRelease(ReleaseLock Lock, SignedEnvelope Envelope, byte[] EnvelopeBytes, ReleaseBundle? Bundle);

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
        if (!string.Equals(pointer.ProductId, productId, StringComparison.Ordinal) || !string.Equals(pointer.Channel, channel, StringComparison.Ordinal)) throw new InvalidDataException("Channel pointer identity does not match its repository path.");
        return new VerifiedChannel(pointer, envelope, bytes);
    }

    public ValueTask<VerifiedRelease> ReadChannelReleaseAsync(string productId, string channel, IReadOnlyDictionary<string, byte[]> trustedKeys, CancellationToken cancellationToken = default)
        => ReadChannelReleaseCoreAsync(productId, channel, trustedKeys, cancellationToken);

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
                            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(item.Value, RepositoryJson.Options);
                            var pin = release.Packages.FirstOrDefault(x => x.Id.Value == item.Key) ?? throw new InvalidDataException($"Bundle contains unpinned manifest '{item.Key}'.");
                            if (ContentHash.Compute(manifestBytes) != pin.ManifestDigest) throw new CryptographicException($"Inline manifest '{item.Key}' failed its pinned digest.");
                            inline[new PackageId(item.Key)] = RepositoryJson.DeserializeManifest(manifestBytes);
                        }
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
        return await ReadReleaseAsync(productId, verifiedChannel.Pointer.ReleaseId, trustedKeys, verifiedChannel.Pointer.ReleaseDigest, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<byte[]> ReadRequiredAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        var result = await store.OpenAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException(key.Value);
        await using (result.ConfigureAwait(false)) return await ReadAllAsync(result.Content, cancellationToken).ConfigureAwait(false);
    }
    private static async ValueTask<byte[]> ReadAllAsync(Stream source, CancellationToken cancellationToken) { using var target = new MemoryStream(); await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false); return target.ToArray(); }
    private sealed record BundleDocument { public required int SchemaVersion { get; init; } public required string LockEnvelope { get; init; } public required Dictionary<string, JsonElement> Inline { get; init; } }
}
