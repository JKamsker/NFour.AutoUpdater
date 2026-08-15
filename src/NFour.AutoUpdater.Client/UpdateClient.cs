namespace NFour.AutoUpdater.Client;

/// <summary>
/// Reusable, non-interactive install/update workflow for launchers. The caller
/// supplies the read transport and an out-of-band trust root; the accepted trust
/// set and anti-rollback state are persisted in the install ledger.
/// </summary>
public sealed class UpdateClient
{
    /// <summary>Resolves, verifies, plans, and atomically applies the requested release layer.</summary>
    /// <param name="store">The repository object store.</param>
    /// <param name="request">The trusted update request.</param>
    /// <param name="progress">An optional sink for apply progress.</param>
    /// <param name="cancellationToken">A token that cancels network and file operations.</param>
    /// <returns>The accepted release and applied plan.</returns>
    public async ValueTask<UpdateClientResult> ApplyAsync(
        IReadableObjectStore store,
        UpdateClientRequest request,
        IApplyProgressSink? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        UpdateClientPolicy.ValidateRequest(request);

        string installRoot = Path.GetFullPath(request.InstallRoot);
        Directory.CreateDirectory(installRoot);
        var ledger = new InstallLedger(installRoot);
        LedgerDocument? previous = await ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
        UpdateClientPolicy.EnsureRepositoryBinding(previous, request);

        Dictionary<string, byte[]> trusted = UpdateClientPolicy.ResolvePinnedTrust(previous, request.TrustedKeys);
        var (descriptor, layout) = await RepositoryFactory.LoadDescriptorAsync(store, cancellationToken)
            .ConfigureAwait(false);
        descriptor.ValidateAgainst(request.RepositoryUri);
        UpdateClientPolicy.EnsureDescriptorTrust(descriptor, trusted);

        var reader = new RepositoryReader(store, descriptor);
        long keySequence = previous?.Lock.KeySequence ?? 0;
        ContentHash? keyManifestDigest = previous?.Lock.KeyManifestDigest;
        try
        {
            VerifiedKeyManifest keys = await reader.ReadKeyManifestAsync(
                trusted,
                keySequence,
                keyManifestDigest,
                request.PinnedRootKeyId,
                UpdateClientPolicy.Now(request),
                cancellationToken).ConfigureAwait(false);
            trusted = keys.Manifest.Keys
                .Where(key => !keys.Manifest.RevokedKeyIds.Contains(key.KeyId, StringComparer.Ordinal))
                .ToDictionary(key => key.KeyId, key => Base64Url.Decode(key.PublicKey), StringComparer.Ordinal);
            keySequence = keys.Manifest.KeySequence;
            keyManifestDigest = ContentHash.Compute(keys.EnvelopeBytes);
        }
        catch (FileNotFoundException)
        {
            // Repositories without key rotation remain valid with the pinned root.
        }

        VerifiedRelease verified = await reader.ReadChannelReleaseAsync(
            request.ProductId,
            request.Channel,
            trusted,
            cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = UpdateClientPolicy.Now(request);
        UpdateClientPolicy.AcceptChannel(
            previous,
            verified,
            now,
            request.ClientVersion ?? UpdateClientPolicy.ClientAssemblyVersion(),
            request.ChannelStalenessBound);

        VerifiedRevocations? revocations = await reader.ReadRevocationsAsync(
            request.ProductId,
            trusted,
            previous?.Lock.RevocationSequence,
            previous?.Lock.RevocationDigest,
            cancellationToken,
            now,
            request.RevocationStalenessBound ?? request.ChannelStalenessBound).ConfigureAwait(false);
        UpdateClientPolicy.EnsureRevocations(previous, verified, revocations);

        ResolutionResult resolution = new VariantResolver().Resolve(verified.Lock, request.Selection);
        UpdateClientPolicy.ThrowForDiagnostics(resolution.Diagnostics, "release selection");
        VariantSelection appliedSelection = request.PackageScope == UpdatePackageScope.Base
            ? new VariantSelection
            {
                Axes = ImmutableSortedDictionary<string, ImmutableSortedSet<string>>.Empty
            }
            : resolution.Selection;
        var repository = new StaticRepository(store, descriptor);
        ComposedFileSet composed = await new FileSetComposer().ComposeAsync(
            repository,
            ScopeResolution(resolution, request.PackageScope),
            cancellationToken).ConfigureAwait(false);
        composed = ScopeFiles(composed, resolution, request.PackageScope);
        UpdateClientPolicy.ThrowForDiagnostics(composed.Diagnostics, "file-set composition");

        IEnumerable<VirtualPath> observedPaths = (previous?.Files.Keys ?? [])
            .Concat(composed.Files.Keys);
        ObservedTreeSnapshot observed = await new LocalTreeScanner().ScanAsync(
            installRoot,
            observedPaths,
            composed.Files.Keys,
            HashPolicy.Changed,
            previous?.Files,
            cancellationToken).ConfigureAwait(false);
        InstallPlan plan = new InstallPlanner().Plan(composed, previous?.Files, observed);
        bool changed = plan.Operations.Any(operation => operation is not FileOperation.Keep);

        Version clientVersion = request.ClientVersion ?? UpdateClientPolicy.ClientAssemblyVersion();
        Version? minimumClientVersion = UpdateClientPolicy.ResolveMinimumClientVersion(descriptor, verified);
        var installLock = new InstallLock
        {
            RepositoryUri = request.RepositoryUri.ToString(),
            ProductId = verified.Lock.ProductId,
            Channel = request.Channel,
            ReleaseId = verified.Lock.ReleaseId,
            ReleaseSequence = verified.Lock.Sequence,
            ReleaseDigest = ContentHash.Compute(verified.EnvelopeBytes),
            Selection = appliedSelection,
            SelectionId = appliedSelection.SelectionId,
            FileSetId = composed.FileSetId,
            LastChannelSequence = verified.Pointer?.ChannelSequence ?? previous?.Lock.LastChannelSequence ?? 0,
            ChannelDigest = verified.PointerEnvelopeBytes is null
                ? previous?.Lock.ChannelDigest
                : ContentHash.Compute(verified.PointerEnvelopeBytes),
            KeySequence = keySequence,
            KeyManifestDigest = keyManifestDigest,
            RevocationSequence = revocations?.Document.RevocationSequence ?? previous?.Lock.RevocationSequence ?? 0,
            RevocationDigest = revocations is null
                ? previous?.Lock.RevocationDigest
                : ContentHash.Compute(revocations.EnvelopeBytes),
            KnownRevocations = revocations?.Document.Entries ?? previous?.Lock.KnownRevocations ?? [],
            TrustedKeyIds = trusted.Keys.OrderBy(key => key, StringComparer.Ordinal).ToImmutableArray(),
            TrustedKeys = trusted.ToImmutableDictionary(
                pair => pair.Key,
                pair => Base64Url.Encode(pair.Value),
                StringComparer.Ordinal),
            AppliedAt = now
        };
        string cacheRoot = UpdateClientPolicy.ResolveCacheRoot(request.CacheRoot);
        HashSet<ContentHash> protectedCacheEntries = previous?.Files.Values
            .Where(file => file.Content is not null)
            .Select(file => file.Content!.Value)
            .ToHashSet() ?? [];
        await new InstallApplier().ApplyAsync(
            installRoot,
            plan,
            composed,
            installLock,
            store,
            layout,
            ledger,
            progress,
            cancellationToken,
            new LocalContentCache(cacheRoot),
            preconditions: new ApplyPreconditions
            {
                MinimumInstalledReleaseSequence = verified.Lock.MinimumInstalledRelease,
                CurrentInstalledReleaseSequence = previous?.Lock.ReleaseSequence,
                ClientVersion = clientVersion,
                MinimumClientVersion = minimumClientVersion
            },
            protectedCacheEntries: protectedCacheEntries).ConfigureAwait(false);

        return new UpdateClientResult(
            verified.Lock.ReleaseId,
            verified.Lock.Sequence,
            composed.FileSetId,
            changed,
            plan,
            appliedSelection);
    }

    private static ResolutionResult ScopeResolution(
        ResolutionResult resolution,
        UpdatePackageScope scope) =>
        scope == UpdatePackageScope.Base
            ? resolution with
            {
                Packages = resolution.Packages
                    .Where(package => package.Requirement.When.IsAlways)
                    .ToImmutableArray()
            }
            : resolution;

    private static ComposedFileSet ScopeFiles(
        ComposedFileSet composed,
        ResolutionResult resolution,
        UpdatePackageScope scope)
    {
        if (scope != UpdatePackageScope.Variants)
            return composed;
        HashSet<PackageId> variants = resolution.Packages
            .Where(package => !package.Requirement.When.IsAlways)
            .Select(package => package.Id)
            .ToHashSet();
        ImmutableSortedDictionary<VirtualPath, ComposedFile> files = composed.Files
            .Where(pair => variants.Contains(pair.Value.Owner))
            .ToImmutableSortedDictionary(pair => pair.Key, pair => pair.Value);
        return composed with { Files = files, FileSetId = FileSetIdentity.Compute(files) };
    }
}
