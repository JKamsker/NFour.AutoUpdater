namespace FourSaas.AutoUpdater.Client;

public enum ApplyPhase { Planning, Fetching, Materialising, Deleting, Committing }
public sealed record ApplyProgress
{
    public required ApplyPhase Phase { get; init; }
    public required long DownloadedBytes { get; init; }
    public required long TotalDownloadBytes { get; init; }
    public required long WrittenBytes { get; init; }
    public required long TotalWriteBytes { get; init; }
    public required int FilesDone { get; init; }
    public required int FilesTotal { get; init; }
    public VirtualPath? Current { get; init; }
    public string? Mirror { get; init; }
}

public interface IApplyProgressSink { ValueTask ReportAsync(ApplyProgress progress, CancellationToken cancellationToken = default); }
public sealed class NullApplyProgressSink : IApplyProgressSink { public ValueTask ReportAsync(ApplyProgress progress, CancellationToken cancellationToken = default) => ValueTask.CompletedTask; }

public sealed class BlobFetcher
{
    public async ValueTask<long> FetchAsync(IReadableObjectStore store, ObjectKey key, ContentHash expected, string stagingPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
        var offset = File.Exists(stagingPath) ? new FileInfo(stagingPath).Length : 0;
        ObjectValidator? validator = null;
        if (offset > 0) validator = (await store.HeadAsync(key, cancellationToken).ConfigureAwait(false))?.Validator;
        if (offset > 0 && validator is not { IsStrong: true }) { File.Delete(stagingPath); offset = 0; }
        await using var response = await store.OpenAsync(key, offset, validator, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Blob '{key}' was not found.");
        if (response.StatusCode == 416 || response.ActualStartOffset != offset)
        {
            File.Delete(stagingPath);
            return await FetchAsync(store, key, expected, stagingPath, cancellationToken).ConfigureAwait(false);
        }
        await using (var target = new FileStream(stagingPath, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await response.Content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        await using var verify = File.OpenRead(stagingPath);
        var actual = await ContentHash.ComputeAsync(verify, expected.Algorithm, cancellationToken).ConfigureAwait(false);
        if (actual != expected) { File.Delete(stagingPath); throw new InvalidDataException($"Blob '{key}' failed SHA-256 verification."); }
        return new FileInfo(stagingPath).Length;
    }
}

public sealed class InstallApplier
{
    private readonly BlobFetcher _fetcher = new();
    public async ValueTask ApplyAsync(
        string installRoot,
        InstallPlan plan,
        ComposedFileSet target,
        InstallLock installLock,
        IReadableObjectStore source,
        RepositoryLayout layout,
        InstallLedger ledger,
        IApplyProgressSink? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress ??= new NullApplyProgressSink();
        ValidateRoot(installRoot);
        var metadata = Path.Combine(installRoot, ".4sup");
        var staging = Path.Combine(metadata, "staging");
        var planPath = Path.Combine(metadata, "plan.json");
        Directory.CreateDirectory(staging);
        await File.WriteAllBytesAsync(planPath, JsonSerializer.SerializeToUtf8Bytes(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new ContentHashJsonConverter(), new PackageIdJsonConverter(), new VirtualPathJsonConverter(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } }), cancellationToken).ConfigureAwait(false);
        var total = plan.BytesToDownload; var downloaded = 0L; var fileNumber = 0;
        await progress.ReportAsync(new ApplyProgress { Phase = ApplyPhase.Fetching, DownloadedBytes = 0, TotalDownloadBytes = total, WrittenBytes = 0, TotalWriteBytes = plan.BytesToWrite, FilesDone = 0, FilesTotal = plan.Operations.Length }, cancellationToken).ConfigureAwait(false);
        foreach (var blob in plan.BlobsToFetch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = layout.Blob(blob.Content); var path = Path.Combine(staging, Convert.ToHexString(blob.Content.Value.Span).ToLowerInvariant());
            downloaded += await _fetcher.FetchAsync(source, key, blob.Content, path, cancellationToken).ConfigureAwait(false);
            await progress.ReportAsync(new ApplyProgress { Phase = ApplyPhase.Fetching, DownloadedBytes = downloaded, TotalDownloadBytes = total, WrittenBytes = 0, TotalWriteBytes = plan.BytesToWrite, FilesDone = fileNumber, FilesTotal = plan.Operations.Length }, cancellationToken).ConfigureAwait(false);
        }

        var nextFiles = new Dictionary<VirtualPath, InstalledFile>();
        var old = await ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (old is not null) foreach (var entry in old.Files) nextFiles[entry.Key] = entry.Value;
        var written = 0L;
        foreach (var operation in plan.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (operation)
            {
                case FileOperation.EnsureDirectory directory:
                    var directoryPath = ResolveManaged(installRoot, directory.Path);
                    if (Directory.Exists(directoryPath) && new DirectoryInfo(directoryPath).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"Managed directory is a reparse point: '{directory.Path}'.");
                    Directory.CreateDirectory(directoryPath);
                    break;
                case FileOperation.Write write:
                    var destination = ResolveManaged(installRoot, write.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    RejectReparseFile(destination);
                    if (write.Policy != FileInstallPolicy.Preserve || !File.Exists(destination))
                    {
                        if (File.Exists(destination)) File.Delete(destination);
                        var staged = Path.Combine(staging, Convert.ToHexString(write.Content.Value.Span).ToLowerInvariant());
                        if (!File.Exists(staged)) throw new InvalidDataException($"Verified staging blob for '{write.Path}' is missing.");
                        if (new FileInfo(staged).Length != write.Size) throw new InvalidDataException($"Verified blob size for '{write.Path}' does not match the manifest.");
                        File.Copy(staged, destination, overwrite: true);
                        if (write.Policy == FileInstallPolicy.Executable && OperatingSystem.IsLinux() || write.Policy == FileInstallPolicy.Executable && OperatingSystem.IsMacOS()) TryMakeExecutable(destination);
                    }
                    nextFiles[write.Path] = new InstalledFile(write.Path, write.Content, write.Size, write.Owner, write.Policy, write.Size, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    written += write.Size;
                    break;
                case FileOperation.Adopt adopt:
                    nextFiles[adopt.Path] = new InstalledFile(adopt.Path, null, 0, adopt.Owner, adopt.Policy, 0, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "adopted", adopt.ObservedContent);
                    break;
                case FileOperation.Delete delete:
                    var deletePath = ResolveManaged(installRoot, delete.Path); RejectReparseFile(deletePath); if (File.Exists(deletePath)) File.Delete(deletePath); nextFiles.Remove(delete.Path); break;
                case FileOperation.Orphan orphan: break;
            }
            fileNumber++;
            await progress.ReportAsync(new ApplyProgress { Phase = operation is FileOperation.Delete ? ApplyPhase.Deleting : ApplyPhase.Materialising, DownloadedBytes = downloaded, TotalDownloadBytes = total, WrittenBytes = written, TotalWriteBytes = plan.BytesToWrite, FilesDone = fileNumber, FilesTotal = plan.Operations.Length, Current = operation.Path }, cancellationToken).ConfigureAwait(false);
        }
        foreach (var file in target.Files.Where(x => x.Value.Kind == FileEntryKind.File))
            if (!nextFiles.ContainsKey(file.Key) && !plan.Operations.Any(x => x.Path == file.Key && x is FileOperation.Orphan)) nextFiles[file.Key] = new InstalledFile(file.Key, file.Value.Content, file.Value.Size, file.Value.Owner, file.Value.Policy, file.Value.Size, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await ledger.CommitAsync(installLock, nextFiles, cancellationToken).ConfigureAwait(false);
        foreach (var file in Directory.EnumerateFiles(staging)) File.Delete(file);
        File.Delete(planPath);
    }

    private static string ResolveManaged(string root, VirtualPath path)
    {
        var full = Path.GetFullPath(Path.Combine(root, path.Value.Replace('/', Path.DirectorySeparatorChar)));
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(canonicalRoot, StringComparison.Ordinal)) throw new IOException("Managed path escapes install root.");
        var current = canonicalRoot[..^1];
        foreach (var component in Path.GetRelativePath(root, Path.GetDirectoryName(full)!).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (Directory.Exists(current) && new DirectoryInfo(current).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"Reparse point in managed path: '{current}'.");
        }
        return full;
    }
    private static void ValidateRoot(string root)
    {
        Directory.CreateDirectory(root);
        var info = new DirectoryInfo(root);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Install root must not be a reparse point.");
        var metadata = Path.Combine(root, ".4sup");
        if (Directory.Exists(metadata) && new DirectoryInfo(metadata).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("The updater metadata directory must not be a reparse point.");
    }
    private static void RejectReparseFile(string path) { if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"Managed file is a reparse point: '{path}'."); }
    private static void TryMakeExecutable(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
#pragma warning disable CA1416
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute); } catch (PlatformNotSupportedException) { }
#pragma warning restore CA1416
    }
}

public sealed class LocalContentCache
{
    private readonly string _root;
    private readonly long _sizeCap;
    public LocalContentCache(string root, long sizeCap = 20L * 1024 * 1024 * 1024) { _root = root; _sizeCap = sizeCap; Directory.CreateDirectory(_root); }
    public string GetPath(ContentHash hash) => Path.Combine(_root, hash.Algorithm.ToString().ToLowerInvariant(), hash.ToString().Split(':')[1][..2], hash.ToString().Split(':')[1][2..4], hash.ToString().Split(':')[1]);
    public async ValueTask<bool> TryGetAsync(ContentHash hash, CancellationToken cancellationToken = default)
    {
        var path = GetPath(hash); if (!File.Exists(path)) return false; await using var stream = File.OpenRead(path); if (await ContentHash.ComputeAsync(stream, hash.Algorithm, cancellationToken).ConfigureAwait(false) != hash) { File.Delete(path); return false; } File.SetLastAccessTimeUtc(path, DateTime.UtcNow); return true;
    }
    public async ValueTask StoreAsync(ContentHash hash, Stream content, CancellationToken cancellationToken = default)
    {
        var path = GetPath(hash); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var verify = File.OpenRead(temporary))
                if (await ContentHash.ComputeAsync(verify, hash.Algorithm, cancellationToken).ConfigureAwait(false) != hash) throw new InvalidDataException($"Local cache object '{hash}' failed content verification.");
            File.Move(temporary, path, overwrite: true);
            await EvictAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async ValueTask EvictAsync(CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Select(x => new FileInfo(x)).OrderBy(x => x.LastAccessTimeUtc).ToList(); var total = files.Sum(x => x.Length); foreach (var file in files) { cancellationToken.ThrowIfCancellationRequested(); if (total <= _sizeCap) break; total -= file.Length; file.Delete(); } await Task.CompletedTask;
    }
}
