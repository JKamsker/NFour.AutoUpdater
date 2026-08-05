namespace FourSaas.AutoUpdater.Publishing;

public sealed class ReleaseBuilder
{
    public ReleaseLock Build(string productId, string releaseId, long sequence, ImmutableArray<AxisDefinition> axes, ImmutableArray<PackageRequirement> requirements, IReadOnlyDictionary<PackageId, PackageManifest> manifests, RepositoryLayout layout, DateTimeOffset createdAt, long? minimumInstalledRelease = null)
    {
        if (!Identifier.IsValid(productId) || !Identifier.IsValid(releaseId)) throw new FormatException("Product and release identifiers must be safe path segments.");
        var pins = ImmutableArray.CreateBuilder<LockedPackage>();
        foreach (var packageId in requirements.Select(x => x.Package).Distinct())
        {
            if (!manifests.TryGetValue(packageId, out var manifest)) throw new InvalidDataException($"No manifest supplied for '{packageId}'.");
            var bytes = RepositoryJson.SerializeManifest(manifest);
            pins.Add(new LockedPackage
            {
                Id = manifest.Id,
                Version = manifest.Version,
                Sequence = manifest.Sequence == 0 ? manifest.Version.Sequence : manifest.Sequence,
                ManifestPath = layout.Package(manifest.Id, manifest.Version).Value,
                ManifestDigest = ContentHash.Compute(bytes),
                FileCount = manifest.FileCount,
                InstallSize = manifest.InstallSize,
                DownloadSize = manifest.DownloadSize,
                Requires = manifest.Requires,
                Conflicts = manifest.Conflicts,
                Overrides = requirements.Where(x => x.Package == packageId).SelectMany(x => x.Overrides).Distinct().ToImmutableArray()
            });
        }
        return new ReleaseLock { SchemaVersion = 1, ProductId = productId, ReleaseId = releaseId, Sequence = sequence, State = ReleaseState.Draft, CreatedAt = createdAt.ToUniversalTime(), Axes = axes, Requirements = requirements, Packages = pins.ToImmutable(), MinimumInstalledRelease = minimumInstalledRelease, CoverageDigest = ContentHash.Compute([]) };
    }

    public ReleaseBundle Bundle(ReleaseLock release, IReadOnlyDictionary<PackageId, PackageManifest> manifests) => new() { Lock = release, Inline = manifests.Where(x => release.Packages.Any(p => p.Id == x.Key)).ToImmutableDictionary(x => x.Key, x => x.Value) };
}

public sealed class ReleaseSigner
{
    public (SignedEnvelope Envelope, byte[] EnvelopeBytes, ContentHash Digest) SignRelease(ReleaseLock release, string keyId, ReadOnlySpan<byte> privateKey)
    {
        var payload = SignedDocument.SerializePayload(release with { Signature = null });
        var envelope = SignedDocument.Sign("release-lock", payload, keyId, privateKey);
        var bytes = SignedDocument.SerializeEnvelope(envelope);
        return (envelope, bytes, ContentHash.Compute(bytes));
    }
    public (SignedEnvelope Envelope, byte[] EnvelopeBytes) SignChannel(ChannelPointer pointer, string keyId, ReadOnlySpan<byte> privateKey)
    {
        var payload = SignedDocument.SerializePayload(pointer);
        var envelope = SignedDocument.Sign("channel-pointer", payload, keyId, privateKey);
        return (envelope, SignedDocument.SerializeEnvelope(envelope));
    }
    public (SignedEnvelope Envelope, byte[] EnvelopeBytes) SignRevocations(RevocationDocument document, string keyId, ReadOnlySpan<byte> privateKey)
    {
        var envelope = SignedDocument.Sign("revocation", SignedDocument.SerializePayload(document), keyId, privateKey);
        return (envelope, SignedDocument.SerializeEnvelope(envelope));
    }
}

public sealed class ChannelAuthoring
{
    public ChannelPointer Promote(string productId, string channel, long channelSequence, long previousChannelSequence, string releaseId, long releaseSequence, ContentHash releaseDigest, DateTimeOffset updatedAt) => new() { SchemaVersion = 1, ProductId = productId, Channel = channel, ChannelSequence = channelSequence, SupersedesChannelSequence = previousChannelSequence, ReleaseId = releaseId, ReleaseSequence = releaseSequence, ReleaseDigest = releaseDigest, UpdatedAt = updatedAt.ToUniversalTime() };
    public ChannelPointer Rollback(ChannelPointer current, string releaseId, long releaseSequence, ContentHash releaseDigest, DateTimeOffset updatedAt) => Promote(current.ProductId, current.Channel, current.ChannelSequence + 1, current.ChannelSequence, releaseId, releaseSequence, releaseDigest, updatedAt) with { Reason = "rollback" };
}
