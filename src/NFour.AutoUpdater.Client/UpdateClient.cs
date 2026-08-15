using System.Security.Cryptography;

namespace NFour.AutoUpdater.Client;

public sealed record UpdateClientRequest
{
    public required Uri RepositoryUri { get; init; }
    public required string ProductId { get; init; }
    public required string Channel { get; init; }
    public required string InstallRoot { get; init; }
    public required VariantSelection Selection { get; init; }
    public required IReadOnlyDictionary<string, byte[]> TrustedKeys { get; init; }
    public string? PinnedRootKeyId { get; init; }
    public string? CacheRoot { get; init; }
    public Version? ClientVersion { get; init; }
    public TimeProvider? TimeProvider { get; init; }
    public TimeSpan ChannelStalenessBound { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan? RevocationStalenessBound { get; init; }
    public UpdatePackageScope PackageScope { get; init; } = UpdatePackageScope.Composed;
}

public enum UpdatePackageScope { Composed, Base, Variants }

public sealed record UpdateClientResult(
    string ReleaseId,
    long ReleaseSequence,
    ContentHash FileSetId,
    bool Changed,
    InstallPlan Plan,
    VariantSelection Selection);

/// <summary>
/// Reusable, non-interactive install/update workflow for launchers. The caller
/// supplies the read transport and an out-of-band trust root; the accepted trust
/// set and anti-rollback state are persisted in the install ledger.
/// </summary>
public sealed class UpdateClient
{
    public async ValueTask<UpdateClientResult> ApplyAsync(
        IReadableObjectStore store,
        UpdateClientRequest request,
        IApplyProgressSink? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        string installRoot = Path.GetFullPath(request.InstallRoot);
        Directory.CreateDirectory(installRoot);
        var ledger = new InstallLedger(installRoot);
        LedgerDocument? previous = await ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
        EnsureRepositoryBinding(previous, request);

        Dictionary<string, byte[]> trusted = ResolvePinnedTrust(previous, request.TrustedKeys);
        var (descriptor, layout) = await RepositoryFactory.LoadDescriptorAsync(store, cancellationToken)
            .ConfigureAwait(false);
        descriptor.ValidateAgainst(request.RepositoryUri);
        EnsureDescriptorTrust(descriptor, trusted);

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
                Now(request),
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
        DateTimeOffset now = Now(request);
        AcceptChannel(
            previous,
            verified,
            now,
            request.ClientVersion ?? ClientAssemblyVersion(),
            request.ChannelStalenessBound);

        VerifiedRevocations? revocations = await reader.ReadRevocationsAsync(
            request.ProductId,
            trusted,
            previous?.Lock.RevocationSequence,
            previous?.Lock.RevocationDigest,
            cancellationToken,
            now,
            request.RevocationStalenessBound ?? request.ChannelStalenessBound).ConfigureAwait(false);
        EnsureRevocations(previous, verified, revocations);

        ResolutionResult resolution = new VariantResolver().Resolve(verified.Lock, request.Selection);
        ThrowForDiagnostics(resolution.Diagnostics, "release selection");
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
        ThrowForDiagnostics(composed.Diagnostics, "file-set composition");

        IEnumerable<VirtualPath> observedPaths = (previous?.Files.Keys ?? [])
            .Concat(composed.Files.Keys);
        ObservedTreeSnapshot observed = await new LocalTreeScanner().ScanAsync(
            installRoot,
            observedPaths,
            composed.Files.Keys,
            HashPolicy.Changed,
            cancellationToken).ConfigureAwait(false);
        InstallPlan plan = new InstallPlanner().Plan(composed, previous?.Files, observed);
        bool changed = plan.Operations.Any(operation => operation is not FileOperation.Keep);

        Version clientVersion = request.ClientVersion ?? ClientAssemblyVersion();
        Version? minimumClientVersion = ResolveMinimumClientVersion(descriptor, verified);
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
        string cacheRoot = ResolveCacheRoot(request.CacheRoot);
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

    private static void ValidateRequest(UpdateClientRequest request)
    {
        if (!request.RepositoryUri.IsAbsoluteUri)
            throw new ArgumentException("RepositoryUri must be absolute.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.InstallRoot))
            throw new ArgumentException("InstallRoot is required.", nameof(request));
        ArgumentNullException.ThrowIfNull(request.Selection);
        ArgumentNullException.ThrowIfNull(request.TrustedKeys);
        if (request.ChannelStalenessBound <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request), "ChannelStalenessBound must be positive.");
        if (request.RevocationStalenessBound is { } revocationBound && revocationBound <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request), "RevocationStalenessBound must be positive.");
        if (!Identifier.IsValid(request.ProductId, "productId", out string? productError))
            throw new FormatException(productError);
        if (!Identifier.IsValid(request.Channel, "channel", out string? channelError))
            throw new FormatException(channelError);
        if (request.TrustedKeys.Count == 0)
            throw new CryptographicException("At least one out-of-band trusted key is required.");
        foreach ((string keyId, byte[] key) in request.TrustedKeys)
        {
            if (!Identifier.IsValid(keyId, "keyId", out string? keyError))
                throw new FormatException(keyError);
            if (key.Length != 32)
                throw new CryptographicException($"Trusted key '{keyId}' is not a 32-byte Ed25519 public key.");
        }
    }

    private static void EnsureRepositoryBinding(LedgerDocument? previous, UpdateClientRequest request)
    {
        if (previous is null)
            return;
        if (!Uri.TryCreate(previous.Lock.RepositoryUri, UriKind.Absolute, out Uri? previousRepository) ||
            Uri.Compare(previousRepository, request.RepositoryUri, UriComponents.AbsoluteUri,
                UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) != 0 ||
            !string.Equals(previous.Lock.ProductId, request.ProductId, StringComparison.Ordinal))
            throw new ApplyPreconditionException("The install root is pinned to a different updater repository or product.");
    }

    private static Dictionary<string, byte[]> ResolvePinnedTrust(
        LedgerDocument? previous,
        IReadOnlyDictionary<string, byte[]> supplied)
    {
        if (previous?.Lock.TrustedKeys.Count is not > 0)
            return supplied.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);

        var persisted = previous.Lock.TrustedKeys.ToDictionary(
            pair => pair.Key,
            pair => Base64Url.Decode(pair.Value),
            StringComparer.Ordinal);
        foreach ((string keyId, byte[] suppliedKey) in supplied)
            if (persisted.TryGetValue(keyId, out byte[]? persistedKey) &&
                !CryptographicOperations.FixedTimeEquals(persistedKey, suppliedKey))
                throw new CryptographicException($"Configured updater key '{keyId}' differs from the install ledger.");
        return persisted;
    }

    private static void EnsureDescriptorTrust(
        RepositoryDescriptor descriptor,
        IReadOnlyDictionary<string, byte[]> trusted)
    {
        bool match = descriptor.TrustedKeys.Any(key =>
            trusted.TryGetValue(key.KeyId, out byte[]? supplied) &&
            CryptographicOperations.FixedTimeEquals(Base64Url.Decode(key.PublicKey), supplied));
        if (!match)
            throw new CryptographicException("The repository descriptor does not contain a configured trust key.");
    }

    private static void AcceptChannel(
        LedgerDocument? previous,
        VerifiedRelease verified,
        DateTimeOffset now,
        Version clientVersion,
        TimeSpan channelStalenessBound)
    {
        ChannelPointer pointer = verified.Pointer
            ?? throw new InvalidDataException("A channel update did not return its signed channel pointer.");
        bool exactInstalledRelease = previous is not null &&
            pointer.ChannelSequence == previous.Lock.LastChannelSequence &&
            pointer.ReleaseSequence == previous.Lock.ReleaseSequence &&
            string.Equals(pointer.ReleaseId, previous.Lock.ReleaseId, StringComparison.Ordinal) &&
            ContentHash.Compute(verified.EnvelopeBytes) == previous.Lock.ReleaseDigest;
        if (exactInstalledRelease && verified.PointerEnvelopeBytes is not null &&
            (previous!.Lock.ChannelDigest is null ||
             previous.Lock.ChannelDigest == ContentHash.Compute(verified.PointerEnvelopeBytes)))
            return;
        ChannelAcceptanceResult accepted = ControlDocumentPolicy.AcceptChannel(
            pointer,
            verified.Lock.ProductId,
            pointer.Channel,
            previous?.Lock.LastChannelSequence ?? 0,
            now,
            channelStalenessBound,
            previous?.Lock.ReleaseSequence,
            clientVersion);
        if (!accepted.Accepted)
            throw new ApplyPreconditionException(accepted.Error ?? "The update channel failed freshness or replay validation.");
    }

    private static void EnsureRevocations(
        LedgerDocument? previous,
        VerifiedRelease verified,
        VerifiedRevocations? revocations)
    {
        if (previous?.Lock.RevocationSequence > 0 && revocations is null)
            throw new ApplyPreconditionException("The repository revocation document disappeared.");
        if (previous is not null && revocations is not null && previous.Lock.KnownRevocations.Any(
                oldEntry => !revocations.Document.Entries.Contains(oldEntry)))
            throw new ApplyPreconditionException("The repository revocation document forgot a previously accepted entry.");
        RevocationEntry? revoked = revocations?.Document.Entries.LastOrDefault(entry =>
            string.Equals(entry.ReleaseId, verified.Lock.ReleaseId, StringComparison.Ordinal) &&
            string.Equals(entry.Action, "yank", StringComparison.Ordinal));
        if (revoked is not null)
            throw new ApplyPreconditionException(
                $"Release '{verified.Lock.ReleaseId}' is yanked ({revoked.Effect}): {revoked.Reason}");
    }

    private static void ThrowForDiagnostics(IEnumerable<Diagnostic> diagnostics, string operation)
    {
        Diagnostic[] errors = diagnostics.Where(diagnostic => diagnostic.IsError).ToArray();
        if (errors.Length == 0)
            return;
        throw new InvalidDataException(
            $"Updater {operation} failed: " +
            string.Join("; ", errors.Select(error => $"{error.Code} {error.Message}")));
    }

    private static Version? ResolveMinimumClientVersion(
        RepositoryDescriptor descriptor,
        VerifiedRelease verified)
    {
        var versions = new[] { descriptor.MinimumClientVersion, verified.Pointer?.MinimumClientVersion }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Version.TryParse(value, out Version? version)
                ? version
                : throw new ApplyPreconditionException($"Invalid minimum updater version '{value}'."))
            .ToArray();
        return versions.Length == 0 ? null : versions.Max();
    }

    private static string ResolveCacheRoot(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);
        if (OperatingSystem.IsWindows())
        {
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localData, "4Story", "4sup", "cas");
        }
        string cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        return Path.Combine(cache, "4sup", "cas");
    }

    private static DateTimeOffset Now(UpdateClientRequest request) =>
        (request.TimeProvider ?? TimeProvider.System).GetUtcNow();

    private static Version ClientAssemblyVersion() =>
        typeof(UpdateClient).Assembly.GetName().Version ?? new Version(1, 0, 0);

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
