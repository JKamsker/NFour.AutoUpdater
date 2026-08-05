namespace FourSaas.AutoUpdater.Client;

public sealed record InstallLock
{
    public int SchemaVersion { get; init; } = 1;
    public required string RepositoryUri { get; init; }
    public required string ProductId { get; init; }
    public string? Channel { get; init; }
    public required string ReleaseId { get; init; }
    public required ContentHash ReleaseDigest { get; init; }
    public required VariantSelection Selection { get; init; }
    public required ContentHash SelectionId { get; init; }
    public required ContentHash FileSetId { get; init; }
    public long LastChannelSequence { get; init; }
    public DateTimeOffset AppliedAt { get; init; }
}

public sealed record LedgerDocument(InstallLock Lock, ImmutableDictionary<VirtualPath, InstalledFile> Files);

public sealed class InstallLedger
{
    private readonly string _path;
    public InstallLedger(string installRoot) { Directory.CreateDirectory(Path.Combine(installRoot, ".4sup")); _path = Path.Combine(installRoot, ".4sup", "state.jsonl"); }
    public async ValueTask<LedgerDocument?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return null;
        var lines = await File.ReadAllLinesAsync(_path, cancellationToken).ConfigureAwait(false);
        if (lines.Length == 0) return null;
        var options = JsonOptions();
        var lockRecord = JsonSerializer.Deserialize<InstallLockRecord>(lines[0], options) ?? throw new FormatException("Install ledger lock is invalid.");
        if (!string.Equals(lockRecord.Kind, "lock", StringComparison.Ordinal) || lockRecord.SchemaVersion != 1) throw new FormatException("Install ledger lock has an unsupported schema or record kind.");
        var files = ImmutableDictionary.CreateBuilder<VirtualPath, InstalledFile>();
        foreach (var line in lines.Skip(1))
        {
            var record = JsonSerializer.Deserialize<FileRecord>(line, options) ?? throw new FormatException("Install ledger file row is invalid.");
            if (!string.Equals(record.Kind, "f", StringComparison.Ordinal)) throw new FormatException("Install ledger contains an unknown record kind.");
            var path = new VirtualPath(record.Path);
            if (files.ContainsKey(path)) throw new FormatException($"Install ledger contains duplicate path '{path}'.");
            files[path] = new InstalledFile(path, record.Hash is null ? null : ContentHash.Parse(record.Hash), record.Size, new PackageId(record.Owner), record.Policy, record.ObservedSize, record.Mtime, record.State ?? "managed", record.ObservedHash is null ? null : ContentHash.Parse(record.ObservedHash));
        }
        var selection = new VariantSelection { Axes = lockRecord.Selection.ToImmutableSortedDictionary(x => x.Key, x => x.Value.ToImmutableSortedSet(StringComparer.Ordinal), StringComparer.Ordinal) };
        return new LedgerDocument(new InstallLock { RepositoryUri = lockRecord.RepositoryUri, ProductId = lockRecord.ProductId, Channel = lockRecord.Channel, ReleaseId = lockRecord.ReleaseId, ReleaseDigest = ContentHash.Parse(lockRecord.ReleaseDigest), Selection = selection, SelectionId = ContentHash.Parse(lockRecord.SelectionId), FileSetId = ContentHash.Parse(lockRecord.FileSetId), LastChannelSequence = lockRecord.LastChannelSequence, AppliedAt = lockRecord.AppliedAt }, files.ToImmutable());
    }

    public async ValueTask CommitAsync(InstallLock installLock, IReadOnlyDictionary<VirtualPath, InstalledFile> files, CancellationToken cancellationToken = default)
    {
        var options = JsonOptions();
        var temp = _path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
        {
            var axes = installLock.Selection.Axes.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.Ordinal);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new InstallLockRecord("lock", 1, installLock.RepositoryUri, installLock.ProductId, installLock.Channel, installLock.ReleaseId, installLock.ReleaseDigest.ToString(), axes, installLock.SelectionId.ToString(), installLock.FileSetId.ToString(), installLock.LastChannelSequence, installLock.AppliedAt.ToUniversalTime()), options)).ConfigureAwait(false);
            foreach (var file in files.Values.OrderBy(x => x.Path.Value, StringComparer.Ordinal)) await writer.WriteLineAsync(JsonSerializer.Serialize(new FileRecord("f", file.Path.Value, file.Content?.ToString(), file.Size, file.Owner.Value, file.Policy, file.ObservedSize, file.ObservedMtimeUnix, file.State, file.ObservedContent?.ToString()), options)).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, _path, true);
    }

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never, Converters = { new ContentHashJsonConverter(), new UtcSecondJsonConverter(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private sealed record InstallLockRecord(
        [property: JsonPropertyName("k")] string Kind,
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("repositoryUri")] string RepositoryUri,
        [property: JsonPropertyName("productId")] string ProductId,
        [property: JsonPropertyName("channel")] string? Channel,
        [property: JsonPropertyName("releaseId")] string ReleaseId,
        [property: JsonPropertyName("releaseDigest")] string ReleaseDigest,
        [property: JsonPropertyName("selection")] Dictionary<string, string[]> Selection,
        [property: JsonPropertyName("selectionId")] string SelectionId,
        [property: JsonPropertyName("fileSetId")] string FileSetId,
        [property: JsonPropertyName("lastChannelSequence")] long LastChannelSequence,
        [property: JsonPropertyName("appliedAt")] DateTimeOffset AppliedAt);
    private sealed record FileRecord(
        [property: JsonPropertyName("k")] string Kind,
        [property: JsonPropertyName("p")] string Path,
        [property: JsonPropertyName("h")] string? Hash,
        [property: JsonPropertyName("s")] long Size,
        [property: JsonPropertyName("o")] string Owner,
        [property: JsonPropertyName("pol")] FileInstallPolicy Policy,
        [property: JsonPropertyName("os")] long ObservedSize,
        [property: JsonPropertyName("mt")] long Mtime,
        [property: JsonPropertyName("st")] string? State,
        [property: JsonPropertyName("oh")] string? ObservedHash);
}
