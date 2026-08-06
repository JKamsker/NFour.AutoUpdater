namespace FourSaas.AutoUpdater.Publishing;

public sealed class ReleaseBuilder
{
    public ReleaseLock Build(string productId, string releaseId, long sequence, ImmutableArray<AxisDefinition> axes, ImmutableArray<PackageRequirement> requirements, IReadOnlyDictionary<PackageId, PackageManifest> manifests, RepositoryLayout layout, DateTimeOffset createdAt, long? minimumInstalledRelease = null, ContentHash? coverageDigest = null)
    {
        if (!Identifier.IsValid(productId, 128) || !Identifier.IsValid(releaseId, 64)) throw new FormatException("Product and release identifiers must be safe path segments.");
        if (coverageDigest is null) throw new InvalidDataException("A release cannot be authored without a verified coverage.json digest. Use BuildAsync or supply coverageDigest.");
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
        var normalizedAxes = axes.Select(static axis => axis.NormalizeRetiredMappings()).ToImmutableArray();
        return new ReleaseLock { SchemaVersion = 1, ProductId = productId, ReleaseId = releaseId, Sequence = sequence, State = ReleaseState.Draft, CreatedAt = createdAt.ToUniversalTime(), Axes = normalizedAxes, Requirements = requirements, Packages = pins.ToImmutable(), MinimumInstalledRelease = minimumInstalledRelease, CoverageDigest = coverageDigest.Value };
    }

    public async ValueTask<(ReleaseLock Release, CoverageDocument Coverage, ContentHash CoverageDigest)> BuildAsync(string productId, string releaseId, long sequence, ImmutableArray<AxisDefinition> axes, ImmutableArray<PackageRequirement> requirements, IReadOnlyDictionary<PackageId, PackageManifest> manifests, RepositoryLayout layout, DateTimeOffset createdAt, IPackageRepository repository, long? minimumInstalledRelease = null, CancellationToken cancellationToken = default)
    {
        var provisional = Build(productId, releaseId, sequence, axes, requirements, manifests, layout, createdAt, minimumInstalledRelease, ContentHash.Compute([]));
        var generated = await new CoverageGenerator().GenerateAsync(provisional, repository, cancellationToken).ConfigureAwait(false);
        return (provisional with { CoverageDigest = generated.Digest }, generated.Document, generated.Digest);
    }

    public async ValueTask WriteCoverageAsync(ReleaseLock release, CoverageDocument coverage, RepositoryLayout layout, IWritableObjectStore destination, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(release.ReleaseId, coverage.ReleaseId, StringComparison.Ordinal)) throw new InvalidDataException("Coverage releaseId does not match the release lock.");
        var digest = ContentHash.Compute(CoverageGenerator.SerializeForDigest(coverage));
        if (digest != release.CoverageDigest) throw new CryptographicException("Coverage bytes do not match the release lock coverageDigest.");
        var bytes = CoverageGenerator.Serialize(coverage with { Digest = digest });
        await using var stream = new MemoryStream(bytes, writable: false);
        if (destination is IConditionalWriteStore conditional)
        {
            var key = layout.Coverage(release.ProductId, release.ReleaseId);
            if (await conditional.PutIfAbsentAsync(key, stream, bytes.LongLength, cancellationToken).ConfigureAwait(false)) return;
            var existing = await destination.OpenAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Coverage object disappeared after an immutable-write race.");
            await using (existing.ConfigureAwait(false))
            {
                var existingBytes = await ReadAllAsync(existing.Content, cancellationToken).ConfigureAwait(false);
                if (!existingBytes.AsSpan().SequenceEqual(bytes)) throw new CryptographicException("Existing coverage object differs from the digest-bound document.");
            }
            return;
        }
        var coverageKey = layout.Coverage(release.ProductId, release.ReleaseId);
        if (await destination.HeadAsync(coverageKey, cancellationToken).ConfigureAwait(false) is not null)
        {
            var existing = await destination.OpenAsync(coverageKey, cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Coverage object disappeared during immutable-write verification.");
            await using (existing.ConfigureAwait(false))
            {
                var existingBytes = await ReadAllAsync(existing.Content, cancellationToken).ConfigureAwait(false);
                if (!existingBytes.AsSpan().SequenceEqual(bytes)) throw new CryptographicException("Existing coverage object differs from the digest-bound document.");
            }
            return;
        }
        await destination.PutAsync(coverageKey, stream, bytes.LongLength, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        return memory.ToArray();
    }

    public ReleaseLock Publish(ReleaseLock draft)
    {
        if (draft.State != ReleaseState.Draft) throw new InvalidDataException("Only draft releases can be published.");
        if (!draft.CoverageDigest.IsValid || draft.CoverageDigest == ContentHash.Compute([])) throw new InvalidDataException("A release must carry a non-empty coverage digest before publication.");
        return draft with { State = ReleaseState.Published, Axes = draft.Axes.Select(static axis => axis.NormalizeRetiredMappings()).ToImmutableArray() };
    }

    public ReleaseBundle Bundle(ReleaseLock release, IReadOnlyDictionary<PackageId, PackageManifest> manifests) => new() { Lock = release, Inline = manifests.Where(x => release.Packages.Any(p => p.Id == x.Key)).ToImmutableDictionary(x => x.Key, x => x.Value) };
}

public sealed class ReleaseSigner
{
    public (SignedEnvelope Envelope, byte[] EnvelopeBytes) SignKeyManifest(KeyManifest manifest, string keyId, ReadOnlySpan<byte> privateKey)
    {
        var envelope = SignedDocument.Sign("key-manifest", SignedDocument.SerializePayload(manifest), keyId, privateKey);
        return (envelope, SignedDocument.SerializeEnvelope(envelope));
    }

    public (SignedEnvelope Envelope, byte[] EnvelopeBytes, ContentHash Digest) SignRelease(ReleaseLock release, string keyId, ReadOnlySpan<byte> privateKey)
    {
        if (release.State != ReleaseState.Published) throw new InvalidDataException("Only a published release lock may be signed for distribution.");
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
    public ChannelPointer Promote(string productId, string channel, long channelSequence, long previousChannelSequence, string releaseId, long releaseSequence, ContentHash releaseDigest, DateTimeOffset updatedAt, string? minimumClientVersion = null) => new() { SchemaVersion = 1, ProductId = productId, Channel = channel, ChannelSequence = channelSequence, SupersedesChannelSequence = previousChannelSequence, ReleaseId = releaseId, ReleaseSequence = releaseSequence, ReleaseDigest = releaseDigest, MinimumClientVersion = minimumClientVersion, UpdatedAt = updatedAt.ToUniversalTime() };
    public ChannelPointer Rollback(ChannelPointer current, string releaseId, long releaseSequence, ContentHash releaseDigest, DateTimeOffset updatedAt) => Promote(current.ProductId, current.Channel, current.ChannelSequence + 1, current.ChannelSequence, releaseId, releaseSequence, releaseDigest, updatedAt, current.MinimumClientVersion) with { Reason = "rollback" };
}
