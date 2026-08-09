using System.Security.Cryptography;

namespace NFour.AutoUpdater.Client;

/// <summary>Identifies the current install-apply phase.</summary>
public enum ApplyPhase
{
    /// <summary>Validating the plan and recovery state.</summary>
    Planning,
    /// <summary>Fetching required content.</summary>
    Fetching,
    /// <summary>Writing target files and directories.</summary>
    Materialising,
    /// <summary>Deleting obsolete managed content.</summary>
    Deleting,
    /// <summary>Committing the install ledger.</summary>
    Committing
}
/// <summary>Controls whether installed content may share storage with the content cache.</summary>
public enum InstallMaterializationProfile
{
    /// <summary>Use a reflink where available and otherwise a private copy.</summary>
    CopyDefault,
    /// <summary>Permit hardlinks when the caller enforces immutable installed files.</summary>
    ImmutableInstall
}
/// <summary>Reports cumulative apply progress.</summary>
public sealed record ApplyProgress
{
    /// <summary>Gets the current apply phase.</summary>
    public required ApplyPhase Phase { get; init; }
    /// <summary>Gets bytes downloaded so far.</summary>
    public required long DownloadedBytes { get; init; }
    /// <summary>Gets total bytes to download.</summary>
    public required long TotalDownloadBytes { get; init; }
    /// <summary>Gets bytes written so far.</summary>
    public required long WrittenBytes { get; init; }
    /// <summary>Gets total bytes to write.</summary>
    public required long TotalWriteBytes { get; init; }
    /// <summary>Gets completed file operations.</summary>
    public required int FilesDone { get; init; }
    /// <summary>Gets total file operations.</summary>
    public required int FilesTotal { get; init; }
    /// <summary>Gets the path currently being processed.</summary>
    public VirtualPath? Current { get; init; }
    /// <summary>Gets the active mirror identifier.</summary>
    public string? Mirror { get; init; }
}
/// <summary>Receives asynchronous install progress updates.</summary>
public interface IApplyProgressSink { /// <summary>Reports one progress snapshot.</summary>
    ValueTask ReportAsync(ApplyProgress progress, CancellationToken cancellationToken = default); }
/// <summary>Discards install progress updates.</summary>
public sealed class NullApplyProgressSink : IApplyProgressSink { /// <inheritdoc />
    public ValueTask ReportAsync(ApplyProgress progress, CancellationToken cancellationToken = default) => ValueTask.CompletedTask; }

/// <summary>Applies an install plan through secure no-follow filesystem operations.</summary>
public sealed class InstallApplier
{
    private readonly BlobFetcher _fetcher = new();
    /// <summary>Fetches, materializes, deletes, and atomically commits an install plan.</summary>
    public async ValueTask ApplyAsync(
        string installRoot,
        InstallPlan plan,
        ComposedFileSet target,
        InstallLock installLock,
        IReadableObjectStore source,
        RepositoryLayout layout,
        InstallLedger ledger,
        IApplyProgressSink? progress = null,
        CancellationToken cancellationToken = default,
        LocalContentCache? cache = null,
        IInstallLockProvider? lockProvider = null,
        ApplyPreconditions? preconditions = null,
        IReadOnlyList<IReadableObjectStore>? mirrors = null,
        IReadOnlySet<ContentHash>? protectedCacheEntries = null,
        InstallMaterializationProfile materializationProfile = InstallMaterializationProfile.CopyDefault)
    {
        progress ??= new NullApplyProgressSink();
        ValidateRoot(installRoot);
        if (preconditions is null) throw new ApplyPreconditionException("ApplyPreconditions are required; the caller must provide the verified protocol preconditions.");
        preconditions.Validate(installRoot, plan);
        lockProvider ??= new FileInstallLockProvider();
        await using var installLease = await lockProvider.AcquireAsync(installRoot, cancellationToken).ConfigureAwait(false);
        var metadata = Path.Combine(installRoot, ".4sup");
        var staging = Path.Combine(metadata, "staging");
        var planPath = Path.Combine(metadata, "plan.json");
        Directory.CreateDirectory(staging);
        var old = await ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
        var marker = RecoveryPlanMarker.Create(target, installLock, plan);
        if (File.Exists(planPath))
        {
            if (old is not null && string.Equals(old.Lock.ReleaseDigest.ToString(), marker.ReleaseDigest, StringComparison.Ordinal) && string.Equals(old.Lock.FileSetId.ToString(), marker.FileSetId, StringComparison.Ordinal) && string.Equals(old.Lock.ProductId, marker.ProductId, StringComparison.Ordinal) && string.Equals(old.Lock.ReleaseId, marker.ReleaseId, StringComparison.Ordinal))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(staging))
                {
                    if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                    else File.Delete(entry);
                }
                File.Delete(planPath);
            }
            else
            {
                var persisted = ReadRecoveryMarker(planPath);
                ValidateRecoveryMarker(persisted, marker);
                plan = persisted.ToInstallPlan();
            }
        }
        else await WriteRecoveryMarkerAsync(planPath, marker, cancellationToken).ConfigureAwait(false);

        // An apply that was interrupted between moving a file aside and completing its
        // replacement leaves a backup behind. Recovery is the point at which those become
        // unreachable, so they are swept here rather than left to accumulate.
        RemoveStaleBackups(installRoot, plan);
        var total = plan.BytesToDownload; var downloaded = 0L; var fetchNumber = 0;
        await progress.ReportAsync(new ApplyProgress { Phase = ApplyPhase.Fetching, DownloadedBytes = 0, TotalDownloadBytes = total, WrittenBytes = 0, TotalWriteBytes = plan.BytesToWrite, FilesDone = 0, FilesTotal = plan.Operations.Length }, cancellationToken).ConfigureAwait(false);
        var configuredParallelism = int.TryParse(Environment.GetEnvironmentVariable("FOURSUP_PARALLELISM"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var configured) ? configured : source.RecommendedParallelism;
        var parallelism = Math.Clamp(configuredParallelism, 1, Math.Max(1, source.RecommendedParallelism));
        using var progressGate = new SemaphoreSlim(1, 1);
        using var cacheGate = new SemaphoreSlim(1, 1);
        var demotedMirrors = new ConcurrentDictionary<IReadableObjectStore, byte>();
        await Parallel.ForEachAsync(plan.BlobsToFetch, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken }, async (blob, token) =>
        {
            token.ThrowIfCancellationRequested();
            var key = layout.Blob(blob.Content); var path = Path.Combine(staging, Convert.ToHexString(blob.Content.Span).ToLowerInvariant());
            long fetchedLength;
            if (cache is not null && await cache.TryGetAsync(blob.Content, token).ConfigureAwait(false))
            {
                File.Copy(cache.GetPath(blob.Content), path, overwrite: true);
                fetchedLength = await VerifyStagedBlobAsync(path, blob.Content, token).ConfigureAwait(false);
            }
            else
            {
                var activeMirrors = mirrors is { Count: > 0 } ? mirrors.Where(x => !demotedMirrors.ContainsKey(x)).ToArray() : [];
                if (mirrors is { Count: > 0 } && activeMirrors.Length == 0) throw new InvalidDataException($"All mirrors were demoted after serving invalid content for '{key}'.");
                fetchedLength = activeMirrors.Length > 0
                    ? await _fetcher.FetchFromMirrorsAsync(activeMirrors, key, blob.Content, blob.Size, path, token, mirror => demotedMirrors.TryAdd(mirror, 0)).ConfigureAwait(false)
                    : await _fetcher.FetchAsync(source, key, blob.Content, blob.Size, path, token).ConfigureAwait(false);
                if (cache is not null)
                {
                    await using var cachedContent = File.OpenRead(path);
                    await cacheGate.WaitAsync(token).ConfigureAwait(false);
                    try { await cache.StoreAsync(blob.Content, cachedContent, token, protectedCacheEntries).ConfigureAwait(false); }
                    finally { cacheGate.Release(); }
                }
            }
            Interlocked.Add(ref downloaded, fetchedLength);
            await progressGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await progress.ReportAsync(new ApplyProgress { Phase = ApplyPhase.Fetching, DownloadedBytes = Interlocked.Read(ref downloaded), TotalDownloadBytes = total, WrittenBytes = 0, TotalWriteBytes = plan.BytesToWrite, FilesDone = Interlocked.Increment(ref fetchNumber), FilesTotal = plan.Operations.Length }, token).ConfigureAwait(false);
            }
            finally { progressGate.Release(); }
        }).ConfigureAwait(false);

        var nextFiles = new Dictionary<VirtualPath, InstalledFile>();
        if (old is not null) foreach (var entry in old.Files) nextFiles[entry.Key] = entry.Value;
        var written = 0L; var fileNumber = 0;
        foreach (var operation in plan.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileIdentity? expectedParent = null;
            var parentSeparator = operation.Path.Value.LastIndexOf('/');
            if (parentSeparator > 0)
            {
                var parentPath = new VirtualPath(operation.Path.Value[..parentSeparator]);
                if (plan.ParentIdentities.TryGetValue(parentPath, out var plannedParent))
                {
                    expectedParent = plannedParent;
                    var parentFullPath = ResolveManaged(installRoot, parentPath);
                    if (!FileIdentityProvider.TryGet(parentFullPath, out var currentParent) || currentParent != plannedParent)
                        throw new ApplyPreconditionException($"Parent directory identity changed before mutating '{operation.Path}'.");
                }
            }
            switch (operation)
            {
                case FileOperation.EnsureDirectory directory:
                    if (SecureInstallOperations.TryEnsureDirectory(installRoot, directory.Path, expectedParent)) break;
                    var directoryPath = ResolveManaged(installRoot, directory.Path);
                    if (Directory.Exists(directoryPath) && new DirectoryInfo(directoryPath).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"Managed directory is a reparse point: '{directory.Path}'.");
                    Directory.CreateDirectory(directoryPath);
                    RejectReparseDirectory(directoryPath);
                    break;
                case FileOperation.Write write:
                    var secureInstall = SecureInstallOperations.Supported;
                    var destination = secureInstall ? null : ResolveManaged(installRoot, write.Path);
                    var shouldWrite = write.Policy != FileInstallPolicy.Preserve || !SecureInstallOperations.TryFileExists(installRoot, write.Path, expectedParent);
                    if (!secureInstall)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination!)!);
                        RejectReparseDirectory(Path.GetDirectoryName(destination!)!);
                        RejectReparseFile(destination!);
                        shouldWrite = write.Policy != FileInstallPolicy.Preserve || !File.Exists(destination!);
                    }
                    if (shouldWrite)
                    {
                        var staged = Path.Combine(staging, Convert.ToHexString(write.Content.Span).ToLowerInvariant());
                        if (!File.Exists(staged)) throw new InvalidDataException($"Verified staging blob for '{write.Path}' is missing.");
                        await VerifyStagedBlobAsync(staged, write.Content, cancellationToken).ConfigureAwait(false);
                        if (new FileInfo(staged).Length != write.Size) throw new InvalidDataException($"Verified blob size for '{write.Path}' does not match the manifest.");
                        // The local CAS is shared and must never be exposed through a
                        // writable hardlink by default. Hardlinks are reserved for
                        // callers that explicitly enforce an immutable install profile.
                        if (!SecureInstallOperations.TryReplaceFile(installRoot, write.Path, staged, PosixFileMode.Resolve(write.Mode, write.Policy), expectedParent, preferHardLink: materializationProfile == InstallMaterializationProfile.ImmutableInstall))
                        {
                            var temporary = destination! + ".4sup-new-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                            try
                            {
                                File.Copy(staged, temporary, overwrite: false);
                                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) TryApplyMode(temporary, PosixFileMode.Resolve(write.Mode, write.Policy));
                                if (File.Exists(destination!))
                                {
                                    var aside = destination + AsideSuffix + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
                                    File.Move(destination!, aside, overwrite: false);
                                    try { File.Move(temporary, destination!, overwrite: false); }
                                    catch
                                    {
                                        if (!File.Exists(destination!) && File.Exists(aside)) File.Move(aside, destination!, overwrite: false);
                                        throw;
                                    }
                                    // The aside copy exists only to roll the replacement back if the
                                    // move above fails. Once the new file is in place it is dead
                                    // weight, and leaving it behind accumulates superseded
                                    // executables, configuration and secrets on every update.
                                    TryDelete(aside);
                                }
                                else File.Move(temporary, destination!, overwrite: false);
                            }
                            finally { if (File.Exists(temporary)) File.Delete(temporary); }
                        }
                    }
                    nextFiles[write.Path] = new InstalledFile(write.Path, write.Content, write.Size, write.Owner, write.Policy, write.Size, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Mode: write.Mode);
                    written += write.Size;
                    break;
                case FileOperation.Adopt adopt:
                    nextFiles[adopt.Path] = new InstalledFile(adopt.Path, null, 0, adopt.Owner, adopt.Policy, 0, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "adopted", adopt.ObservedContent);
                    break;
                case FileOperation.Delete delete:
                    if (!SecureInstallOperations.TryDeleteFile(installRoot, delete.Path, expectedParent))
                    {
                        var deletePath = ResolveManaged(installRoot, delete.Path);
                        RejectReparseFile(deletePath);
                        if (File.Exists(deletePath)) File.Delete(deletePath);
                        PruneEmptyParents(installRoot, Path.GetDirectoryName(deletePath)!);
                    }
                    else
                    {
                        var deletedPath = ResolveManaged(installRoot, delete.Path);
                        PruneEmptyParents(installRoot, Path.GetDirectoryName(deletedPath)!);
                    }
                    nextFiles.Remove(delete.Path);
                    break;
                case FileOperation.Orphan orphan: break;
            }
            fileNumber++;
            await progress.ReportAsync(new ApplyProgress { Phase = operation is FileOperation.Delete ? ApplyPhase.Deleting : ApplyPhase.Materialising, DownloadedBytes = downloaded, TotalDownloadBytes = total, WrittenBytes = written, TotalWriteBytes = plan.BytesToWrite, FilesDone = fileNumber, FilesTotal = plan.Operations.Length, Current = operation.Path }, cancellationToken).ConfigureAwait(false);
        }
        await progress.ReportAsync(new ApplyProgress { Phase = ApplyPhase.Committing, DownloadedBytes = downloaded, TotalDownloadBytes = total, WrittenBytes = written, TotalWriteBytes = plan.BytesToWrite, FilesDone = fileNumber, FilesTotal = plan.Operations.Length }, cancellationToken).ConfigureAwait(false);
        foreach (var file in target.Files.Where(x => x.Value.Kind == FileEntryKind.File))
            if (!nextFiles.ContainsKey(file.Key) && !plan.Operations.Any(x => x.Path == file.Key && x is FileOperation.Orphan)) nextFiles[file.Key] = new InstalledFile(file.Key, file.Value.Content, file.Value.Size, file.Value.Owner, file.Value.Policy, file.Value.Size, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await ledger.CommitAsync(installLock, nextFiles, cancellationToken).ConfigureAwait(false);
        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)) File.Delete(file);
        File.Delete(planPath);
    }

    /// <summary>Suffix marking a superseded file kept only to roll a replacement back.</summary>
    private const string AsideSuffix = ".old-";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Removes rollback copies left by a previous, interrupted apply for paths this plan is
    /// about to rewrite. Scoped to the plan's own write targets so unrelated files that merely
    /// happen to end in the suffix are never touched.
    /// </summary>
    private static void RemoveStaleBackups(string installRoot, InstallPlan plan)
    {
        foreach (var write in plan.Operations.OfType<FileOperation.Write>())
        {
            string destination;
            try { destination = ResolveManaged(installRoot, write.Path); }
            catch (IOException) { continue; }
            var directory = Path.GetDirectoryName(destination);
            if (directory is null || !Directory.Exists(directory)) continue;
            var prefix = Path.GetFileName(destination) + AsideSuffix;
            foreach (var candidate in Directory.EnumerateFiles(directory, prefix + "*"))
                if (Path.GetFileName(candidate).StartsWith(prefix, StringComparison.Ordinal)) TryDelete(candidate);
        }
    }

    private static async ValueTask<long> VerifyStagedBlobAsync(string path, ContentHash expected, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var actual = await ContentHash.ComputeAsync(stream, expected.Algorithm, cancellationToken).ConfigureAwait(false);
        if (actual != expected) throw new InvalidDataException($"Staged blob '{expected}' failed its local content verification.");
        return stream.Length;
    }

    private static async ValueTask WriteRecoveryMarkerAsync(string path, RecoveryPlanMarker marker, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, marker, options, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static RecoveryPlanMarker ReadRecoveryMarker(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            JsonRules.Validate(bytes);
            return JsonSerializer.Deserialize<RecoveryPlanMarker>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new FormatException("Recovery plan is empty.");
        }
        catch (Exception ex) { throw new ApplyPreconditionException($"The unfinished apply plan is corrupt: {ex.Message}"); }
    }

    private static void ValidateRecoveryMarker(RecoveryPlanMarker actual, RecoveryPlanMarker expected)
    {
        if (actual.SchemaVersion != expected.SchemaVersion || !string.Equals(actual.FileSetId, expected.FileSetId, StringComparison.Ordinal) || !string.Equals(actual.ReleaseDigest, expected.ReleaseDigest, StringComparison.Ordinal) || !string.Equals(actual.ProductId, expected.ProductId, StringComparison.Ordinal) || !string.Equals(actual.ReleaseId, expected.ReleaseId, StringComparison.Ordinal) || !actual.Blobs.SequenceEqual(expected.Blobs) || actual.BytesToDownload != expected.BytesToDownload || actual.BytesToWrite != expected.BytesToWrite || actual.NetInstallDelta != expected.NetInstallDelta || !actual.Operations.SequenceEqual(expected.Operations) || !actual.ParentIdentities.OrderBy(x => x.Key, StringComparer.Ordinal).SequenceEqual(expected.ParentIdentities.OrderBy(x => x.Key, StringComparer.Ordinal) ) || !string.Equals(actual.RootIdentity, expected.RootIdentity, StringComparison.Ordinal))
            throw new ApplyPreconditionException("An unfinished apply belongs to a different release, file set, or operation plan; recover it before starting another operation.");
    }

    private sealed record RecoveryBlob(string Content, long Size);

    private sealed record RecoveryPlanMarker(int SchemaVersion, string FileSetId, string ReleaseDigest, string ProductId, string ReleaseId, RecoveryBlob[] Blobs, RecoveryOperation[] Operations, long BytesToDownload, long BytesToWrite, long NetInstallDelta, DateTimeOffset CreatedAt)
    {
        public Dictionary<string, string> ParentIdentities { get; init; } = new(StringComparer.Ordinal);
        public string? RootIdentity { get; init; }

        // Schema 2 records each blob's declared size alongside its digest. A recovered plan
        // must be able to bound its downloads exactly as the original plan did; a marker that
        // stored only digests would silently drop that bound on resume.
        public static RecoveryPlanMarker Create(ComposedFileSet target, InstallLock installLock, InstallPlan plan) => new(2, target.FileSetId.ToString(), installLock.ReleaseDigest.ToString(), installLock.ProductId, installLock.ReleaseId, plan.BlobsToFetch.Select(x => new RecoveryBlob(x.Content.ToString(), x.Size)).OrderBy(x => x.Content, StringComparer.Ordinal).ToArray(), plan.Operations.Select(RecoveryOperation.From).ToArray(), plan.BytesToDownload, plan.BytesToWrite, plan.NetInstallDelta, DateTimeOffset.UtcNow)
        {
            ParentIdentities = plan.ParentIdentities.ToDictionary(x => x.Key.Value, x => x.Value.Value, StringComparer.Ordinal),
            RootIdentity = plan.RootIdentity?.Value
        };

        public InstallPlan ToInstallPlan() => new()
        {
            Operations = Operations.Select(x => x.ToOperation()).ToImmutableArray(),
            BlobsToFetch = Blobs.Select(x => new BlobLocator(ContentHash.Parse(x.Content), x.Size)).ToImmutableArray(),
            BytesToDownload = BytesToDownload,
            BytesToWrite = BytesToWrite,
            PeakFreeSpaceRequiredByVolume = ImmutableDictionary<string, long>.Empty,
            NetInstallDelta = NetInstallDelta,
            ParentIdentities = ParentIdentities.Where(x => VirtualPath.TryCreate(x.Key, out _, out _)).ToImmutableDictionary(x => new VirtualPath(x.Key), x => new FileIdentity(x.Value)),
            RootIdentity = RootIdentity is null ? null : new FileIdentity(RootIdentity)
        };
    }

    private sealed record RecoveryOperation(string Kind, string Path, string? Content, long Size, string? Owner, FileInstallPolicy? Policy, string? ObservedContent, string? Reason, string? Mode = null)
    {
        public static RecoveryOperation From(FileOperation operation) => operation switch
        {
            FileOperation.Write write => new("write", write.Path.Value, write.Content.ToString(), write.Size, write.Owner.Value, write.Policy, null, null, write.Mode),
            FileOperation.Delete delete => new("delete", delete.Path.Value, null, 0, delete.PreviousOwner.Value, null, null, null),
            FileOperation.Keep keep => new("keep", keep.Path.Value, null, 0, null, null, null, null),
            FileOperation.Orphan orphan => new("orphan", orphan.Path.Value, null, 0, orphan.PreviousOwner.Value, null, null, orphan.Reason),
            FileOperation.Adopt adopt => new("adopt", adopt.Path.Value, null, 0, adopt.Owner.Value, adopt.Policy, adopt.ObservedContent?.ToString(), null),
            FileOperation.EnsureDirectory directory => new("directory", directory.Path.Value, null, 0, null, null, null, null),
            _ => throw new InvalidDataException("Unknown file operation in recovery plan.")
        };

        public FileOperation ToOperation()
        {
            if (!VirtualPath.TryCreate(Path, out var path, out var error)) throw new FormatException(error);
            return Kind switch
            {
                "write" when ContentHash.TryParse(Content, out var hash) && PackageId.TryCreate(Owner, out var owner) => new FileOperation.Write(path, hash, Size, owner, Policy ?? FileInstallPolicy.Replace, Mode),
                "delete" when PackageId.TryCreate(Owner, out var deleteOwner) => new FileOperation.Delete(path, deleteOwner),
                "keep" => new FileOperation.Keep(path),
                "orphan" when PackageId.TryCreate(Owner, out var orphanOwner) => new FileOperation.Orphan(path, orphanOwner, Reason ?? "Recovered orphan"),
                "adopt" when PackageId.TryCreate(Owner, out var adoptOwner) => new FileOperation.Adopt(path, adoptOwner, Policy ?? FileInstallPolicy.Preserve, ContentHash.TryParse(ObservedContent, out var observed) ? observed : null),
                "directory" => new FileOperation.EnsureDirectory(path),
                _ => throw new FormatException($"Invalid recovery operation '{Kind}'.")
            };
        }
    }

    private static string ResolveManaged(string root, VirtualPath path)
    {
        if (path.Value.Equals(".4sup", StringComparison.Ordinal) || path.Value.StartsWith(".4sup/", StringComparison.Ordinal)) throw new IOException("The updater metadata directory is not a managed install path.");
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
    private static void RejectReparseDirectory(string path) { if (Directory.Exists(path) && new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"Managed directory is a reparse point: '{path}'."); }
    private static void PruneEmptyParents(string root, string directory)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = Path.GetFullPath(directory);
        while (!string.Equals(current, normalizedRoot, StringComparison.OrdinalIgnoreCase) && !string.Equals(current, Path.Combine(normalizedRoot, ".4sup"), StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(current)) { current = Path.GetDirectoryName(current)!; continue; }
            RejectReparseDirectory(current);
            if (Directory.EnumerateFileSystemEntries(current).Any()) break;
            Directory.Delete(current);
            current = Path.GetDirectoryName(current)!;
        }
    }
    /// <summary>
    /// Applies resolved permission bits on the fallback (non-handle-relative) write path.
    /// Replaces the previous hardcoded 0755-for-executables behaviour, which ignored whatever
    /// the manifest actually declared.
    /// </summary>
    private static void TryApplyMode(string path, uint mode)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
#pragma warning disable CA1416
        try { File.SetUnixFileMode(path, (UnixFileMode)(mode & 0xFFF)); } catch (PlatformNotSupportedException) { }
#pragma warning restore CA1416
    }
}

