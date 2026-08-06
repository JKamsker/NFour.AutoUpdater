using System.Runtime.InteropServices;

namespace FourSaas.AutoUpdater.Client;

public sealed record InstallLock
{
    public int SchemaVersion { get; init; } = 1;
    public required string RepositoryUri { get; init; }
    public required string ProductId { get; init; }
    public string? Channel { get; init; }
    public required string ReleaseId { get; init; }
    public long ReleaseSequence { get; init; }
    public required ContentHash ReleaseDigest { get; init; }
    public required VariantSelection Selection { get; init; }
    public required ContentHash SelectionId { get; init; }
    public required ContentHash FileSetId { get; init; }
    public long LastChannelSequence { get; init; }
    public long KeySequence { get; init; }
    public ContentHash? KeyManifestDigest { get; init; }
    public long RevocationSequence { get; init; }
    public ContentHash? RevocationDigest { get; init; }
    public ImmutableArray<RevocationEntry> KnownRevocations { get; init; } = [];
    public ImmutableArray<string> TrustedKeyIds { get; init; } = [];
    // The public-key material is persisted with the ledger so a later invocation cannot
    // silently replace the trust anchor merely by supplying a different CLI argument.
    // It is public material, not a secret; private signing keys never enter the ledger.
    public ImmutableDictionary<string, string> TrustedKeys { get; init; } = ImmutableDictionary<string, string>.Empty;
    public DateTimeOffset AppliedAt { get; init; }
    [JsonIgnore]
    public ImmutableDictionary<string, JsonElement> UnknownFields { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;
}

public sealed record LedgerDocument(InstallLock Lock, ImmutableDictionary<VirtualPath, InstalledFile> Files);

public sealed class InstallLedger
{
    private readonly string _root;
    private readonly string _path;
    private readonly string _historyDirectory;
    public InstallLedger(string installRoot)
    {
        _root = Path.GetFullPath(installRoot);
        _path = Path.Combine(_root, ".4sup", "state.jsonl");
        _historyDirectory = Path.Combine(_root, ".4sup", "history");
    }
    public ValueTask<LedgerDocument?> ReadAsync(CancellationToken cancellationToken = default) => ReadPathAsync(_path, cancellationToken);
    public async ValueTask<IReadOnlyList<LedgerDocument>> ReadHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_historyDirectory)) return [];
        var result = new List<LedgerDocument>();
        foreach (var path in Directory.EnumerateFiles(_historyDirectory, "*.jsonl").OrderBy(x => x, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = await ReadPathAsync(path, cancellationToken).ConfigureAwait(false);
            if (document is not null) result.Add(document);
        }
        return result;
    }

    private static async ValueTask<LedgerDocument?> ReadPathAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        if (lines.Length == 0) return null;
        var options = JsonOptions();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) throw new FormatException("Install ledger contains an empty record.");
            JsonRules.Validate(Encoding.UTF8.GetBytes(line));
        }
        var lockRecord = JsonSerializer.Deserialize<InstallLockRecord>(lines[0], options) ?? throw new FormatException("Install ledger lock is invalid.");
        if (!string.Equals(lockRecord.Kind, "lock", StringComparison.Ordinal) || lockRecord.SchemaVersion != 1) throw new FormatException("Install ledger lock has an unsupported schema or record kind.");
        var files = ImmutableDictionary.CreateBuilder<VirtualPath, InstalledFile>();
        foreach (var line in lines.Skip(1))
        {
            var record = JsonSerializer.Deserialize<FileRecord>(line, options) ?? throw new FormatException("Install ledger file row is invalid.");
            if (!string.Equals(record.Kind, "f", StringComparison.Ordinal)) throw new FormatException("Install ledger contains an unknown record kind.");
            var filePath = new VirtualPath(record.Path);
            if (files.ContainsKey(filePath)) throw new FormatException($"Install ledger contains duplicate path '{filePath}'.");
            files[filePath] = new InstalledFile(filePath, record.Hash is null ? null : ContentHash.Parse(record.Hash), record.Size, new PackageId(record.Owner), record.Policy ?? FileInstallPolicy.Replace, record.ObservedSize, record.Mtime, record.State ?? "managed", record.ObservedHash is null ? null : ContentHash.Parse(record.ObservedHash), record.ExtensionData?.ToImmutableDictionary(StringComparer.Ordinal));
        }
        var selection = new VariantSelection { Axes = lockRecord.Selection.ToImmutableSortedDictionary(x => x.Key, x => x.Value.ToImmutableSortedSet(StringComparer.Ordinal), StringComparer.Ordinal) };
        var trustedKeys = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (lockRecord.TrustedKeys is not null)
            foreach (var pair in lockRecord.TrustedKeys)
            {
                if (!Identifier.IsValid(pair.Key, "keyId", out var keyError)) throw new FormatException(keyError);
                var key = Base64Url.Decode(pair.Value);
                if (key.Length != 32) throw new FormatException($"Ledger trust key '{pair.Key}' is not a 32-byte Ed25519 public key.");
                trustedKeys[pair.Key] = Base64Url.Encode(key);
            }
        return new LedgerDocument(new InstallLock { RepositoryUri = lockRecord.RepositoryUri, ProductId = lockRecord.ProductId, Channel = lockRecord.Channel, ReleaseId = lockRecord.ReleaseId, ReleaseSequence = lockRecord.ReleaseSequence, ReleaseDigest = ContentHash.Parse(lockRecord.ReleaseDigest), Selection = selection, SelectionId = ContentHash.Parse(lockRecord.SelectionId), FileSetId = ContentHash.Parse(lockRecord.FileSetId), LastChannelSequence = lockRecord.LastChannelSequence, KeySequence = lockRecord.KeySequence, KeyManifestDigest = lockRecord.KeyManifestDigest is null ? null : ContentHash.Parse(lockRecord.KeyManifestDigest), RevocationSequence = lockRecord.RevocationSequence, RevocationDigest = lockRecord.RevocationDigest is null ? null : ContentHash.Parse(lockRecord.RevocationDigest), KnownRevocations = lockRecord.KnownRevocations?.ToImmutableArray() ?? [], TrustedKeyIds = lockRecord.TrustedKeyIds?.ToImmutableArray() ?? [], TrustedKeys = trustedKeys.ToImmutable(), AppliedAt = lockRecord.AppliedAt, UnknownFields = lockRecord.ExtensionData?.ToImmutableDictionary(StringComparer.Ordinal) ?? ImmutableDictionary<string, JsonElement>.Empty }, files.ToImmutable());
    }

    public async ValueTask CommitAsync(InstallLock installLock, IReadOnlyDictionary<VirtualPath, InstalledFile> files, CancellationToken cancellationToken = default)
    {
        var options = JsonOptions();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (File.Exists(_path))
        {
            var current = await ReadPathAsync(_path, cancellationToken).ConfigureAwait(false);
            if (current is not null)
            {
                Directory.CreateDirectory(_historyDirectory);
                var historyPath = Path.Combine(_historyDirectory, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".jsonl");
                File.Copy(_path, historyPath, overwrite: false);
            }
        }
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                var axes = installLock.Selection.Axes.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.Ordinal);
                var lockRecord = new InstallLockRecord("lock", 1, installLock.RepositoryUri, installLock.ProductId, installLock.Channel, installLock.ReleaseId, installLock.ReleaseSequence, installLock.ReleaseDigest.ToString(), axes, installLock.SelectionId.ToString(), installLock.FileSetId.ToString(), installLock.LastChannelSequence, installLock.KeySequence, installLock.KeyManifestDigest?.ToString(), installLock.RevocationSequence, installLock.RevocationDigest?.ToString(), installLock.KnownRevocations.ToArray(), installLock.TrustedKeyIds.ToArray(), installLock.TrustedKeys.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal), installLock.AppliedAt.ToUniversalTime())
                {
                    ExtensionData = installLock.UnknownFields.IsEmpty ? null : installLock.UnknownFields.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal)
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(lockRecord, options)).ConfigureAwait(false);
                foreach (var file in files.Values.OrderBy(x => x.Path.Value, StringComparer.Ordinal))
                {
                    FileInstallPolicy? policy = file.Policy == FileInstallPolicy.Replace ? null : file.Policy;
                    var state = string.Equals(file.State, "managed", StringComparison.Ordinal) ? null : file.State;
                    var fileRecord = new FileRecord("f", file.Path.Value, file.Content?.ToString(), file.Size, file.Owner.Value, policy, file.ObservedSize, file.ObservedMtimeUnix, state, file.ObservedContent?.ToString())
                    {
                        ExtensionData = file.UnknownFields is null || file.UnknownFields.IsEmpty ? null : file.UnknownFields.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal)
                    };
                    await writer.WriteLineAsync(JsonSerializer.Serialize(fileRecord, options)).ConfigureAwait(false);
                }
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, true);
            FlushContainingDirectory();
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Converters = { new ContentHashJsonConverter(), new UtcSecondJsonConverter(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    private void FlushContainingDirectory()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(directory)) return;
        var descriptor = OpenDirectory(directory, PosixPlatform.O_RDONLY | PosixPlatform.O_DIRECTORY | PosixPlatform.O_CLOEXEC);
        if (descriptor < 0) return;
        try { _ = FlushFile(descriptor); } finally { CloseFile(descriptor); }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int OpenDirectory(string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)] private static extern int FlushFile(int descriptor);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)] private static extern int CloseFile(int descriptor);
    private sealed record InstallLockRecord(
        [property: JsonPropertyName("k")] string Kind,
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("repositoryUri")] string RepositoryUri,
        [property: JsonPropertyName("productId")] string ProductId,
        [property: JsonPropertyName("channel")] string? Channel,
        [property: JsonPropertyName("releaseId")] string ReleaseId,
        [property: JsonPropertyName("releaseSequence")] long ReleaseSequence,
        [property: JsonPropertyName("releaseDigest")] string ReleaseDigest,
        [property: JsonPropertyName("selection")] Dictionary<string, string[]> Selection,
        [property: JsonPropertyName("selectionId")] string SelectionId,
        [property: JsonPropertyName("fileSetId")] string FileSetId,
        [property: JsonPropertyName("lastChannelSequence")] long LastChannelSequence,
        [property: JsonPropertyName("keySequence")] long KeySequence,
        [property: JsonPropertyName("keyManifestDigest")] string? KeyManifestDigest,
        [property: JsonPropertyName("revocationSequence")] long RevocationSequence,
        [property: JsonPropertyName("revocationDigest")] string? RevocationDigest,
        [property: JsonPropertyName("knownRevocations")] RevocationEntry[]? KnownRevocations,
        [property: JsonPropertyName("trustedKeyIds")] string[]? TrustedKeyIds,
        [property: JsonPropertyName("trustedKeys")] Dictionary<string, string>? TrustedKeys,
        [property: JsonPropertyName("appliedAt")] DateTimeOffset AppliedAt)
    {
        [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
    }
    private sealed record FileRecord(
        [property: JsonPropertyName("k")] string Kind,
        [property: JsonPropertyName("p")] string Path,
        [property: JsonPropertyName("h")] string? Hash,
        [property: JsonPropertyName("s")] long Size,
        [property: JsonPropertyName("o")] string Owner,
        [property: JsonPropertyName("pol")] FileInstallPolicy? Policy,
        [property: JsonPropertyName("os")] long ObservedSize,
        [property: JsonPropertyName("mt")] long Mtime,
        [property: JsonPropertyName("st")] string? State,
        [property: JsonPropertyName("oh")] string? ObservedHash)
    {
        [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
    }
}
