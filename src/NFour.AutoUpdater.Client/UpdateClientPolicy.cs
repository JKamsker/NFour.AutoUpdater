using System.Security.Cryptography;

namespace NFour.AutoUpdater.Client;

internal static class UpdateClientPolicy
{
    internal static void ValidateRequest(UpdateClientRequest request)
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

    internal static void EnsureRepositoryBinding(LedgerDocument? previous, UpdateClientRequest request)
    {
        if (previous is null)
            return;
        if (!Uri.TryCreate(previous.Lock.RepositoryUri, UriKind.Absolute, out Uri? previousRepository) ||
            Uri.Compare(previousRepository, request.RepositoryUri, UriComponents.AbsoluteUri,
                UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) != 0 ||
            !string.Equals(previous.Lock.ProductId, request.ProductId, StringComparison.Ordinal))
            throw new ApplyPreconditionException("The install root is pinned to a different updater repository or product.");
    }

    internal static Dictionary<string, byte[]> ResolvePinnedTrust(
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

    internal static void EnsureDescriptorTrust(
        RepositoryDescriptor descriptor,
        IReadOnlyDictionary<string, byte[]> trusted)
    {
        bool match = descriptor.TrustedKeys.Any(key =>
            trusted.TryGetValue(key.KeyId, out byte[]? supplied) &&
            CryptographicOperations.FixedTimeEquals(Base64Url.Decode(key.PublicKey), supplied));
        if (!match)
            throw new CryptographicException("The repository descriptor does not contain a configured trust key.");
    }

    internal static void AcceptChannel(
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

    internal static void EnsureRevocations(
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

    internal static void ThrowForDiagnostics(IEnumerable<Diagnostic> diagnostics, string operation)
    {
        Diagnostic[] errors = diagnostics.Where(diagnostic => diagnostic.IsError).ToArray();
        if (errors.Length == 0)
            return;
        throw new InvalidDataException(
            $"Updater {operation} failed: " +
            string.Join("; ", errors.Select(error => $"{error.Code} {error.Message}")));
    }

    internal static Version? ResolveMinimumClientVersion(
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

    internal static string ResolveCacheRoot(string? configured)
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

    internal static DateTimeOffset Now(UpdateClientRequest request) =>
        (request.TimeProvider ?? TimeProvider.System).GetUtcNow();

    internal static Version ClientAssemblyVersion() =>
        typeof(UpdateClient).Assembly.GetName().Version ?? new Version(1, 0, 0);
}
