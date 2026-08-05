using System.Security.Cryptography;

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
        var metadataPath = stagingPath + ".resume.json";

        if (File.Exists(stagingPath))
        {
            try
            {
                await using var completeCandidate = File.OpenRead(stagingPath);
                if (await ContentHash.ComputeAsync(completeCandidate, expected.Algorithm, cancellationToken).ConfigureAwait(false) == expected)
                {
                    DeleteIfPresent(metadataPath);
                    return new FileInfo(stagingPath).Length;
                }
            }
            catch (IOException) { }
        }

        var offset = File.Exists(stagingPath) ? new FileInfo(stagingPath).Length : 0;
        var validator = offset == 0 ? null : ReadValidator(metadataPath, expected);
        if (offset > 0 && validator is not { IsStrong: true })
        {
            DeleteIfPresent(stagingPath);
            DeleteIfPresent(metadataPath);
            offset = 0;
            validator = null;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var response = await store.OpenAsync(key, offset, validator, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Blob '{key}' was not found.");
            var restart = response.StatusCode == 416 || response.ActualStartOffset != offset || (offset > 0 && response.StatusCode == 200);
            if (restart)
            {
                DeleteIfPresent(stagingPath);
                DeleteIfPresent(metadataPath);
                offset = 0;
                validator = null;
                continue;
            }
            if (offset > 0 && response.StatusCode != 206)
                throw new IOException($"The storage backend did not honor the validated range for '{key}'.");
            if (offset == 0 && response.Validator is { } initialValidator)
            {
                validator = initialValidator;
                await WriteValidatorAsync(metadataPath, expected, initialValidator, cancellationToken).ConfigureAwait(false);
            }

            await using (var target = new FileStream(stagingPath, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await response.Content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);

            await using var verify = File.OpenRead(stagingPath);
            var actual = await ContentHash.ComputeAsync(verify, expected.Algorithm, cancellationToken).ConfigureAwait(false);
            if (actual != expected)
            {
                DeleteIfPresent(stagingPath);
                DeleteIfPresent(metadataPath);
                throw new InvalidDataException($"Blob '{key}' failed content verification.");
            }
            DeleteIfPresent(metadataPath);
            return new FileInfo(stagingPath).Length;
        }

        throw new IOException($"The storage backend changed or refused the validator for '{key}' while resuming.");
    }

    public async ValueTask<long> FetchFromMirrorsAsync(IReadOnlyList<IReadableObjectStore> mirrors, ObjectKey key, ContentHash expected, string stagingPath, CancellationToken cancellationToken = default, Action<IReadableObjectStore>? demoteMirror = null)
    {
        if (mirrors.Count == 0) throw new ArgumentException("At least one mirror is required.", nameof(mirrors));
        Exception? last = null;
        var integrityFailure = false;
        foreach (var mirror in mirrors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await FetchAsync(mirror, key, expected, stagingPath, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException)
            {
                last = ex;
                integrityFailure |= ex is InvalidDataException or CryptographicException;
                if (ex is InvalidDataException or CryptographicException) demoteMirror?.Invoke(mirror);
                if (ex is InvalidDataException or CryptographicException)
                {
                    TryDelete(stagingPath);
                    TryDelete(stagingPath + ".resume.json");
                }
            }
        }
        if (integrityFailure) throw new InvalidDataException($"All mirrors failed content verification for '{key}'.", last);
        throw new IOException($"All mirrors failed for '{key}'.", last);
    }

    private static ObjectValidator? ReadValidator(string path, ContentHash expected)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var metadata = JsonSerializer.Deserialize<ResumeMetadata>(File.ReadAllBytes(path));
            return metadata is not null && string.Equals(metadata.ExpectedHash, expected.ToString(), StringComparison.Ordinal) && metadata.IsStrong
                ? new ObjectValidator(metadata.Kind, metadata.Value, metadata.IsStrong)
                : null;
        }
        catch (Exception) { return null; }
    }

    private static async ValueTask WriteValidatorAsync(string path, ContentHash expected, ObjectValidator validator, CancellationToken cancellationToken)
    {
        var metadata = new ResumeMetadata(expected.ToString(), validator.Kind, validator.Value, validator.IsStrong);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static void DeleteIfPresent(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    private sealed record ResumeMetadata(string ExpectedHash, ObjectValidatorKind Kind, string Value, bool IsStrong);
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
        CancellationToken cancellationToken = default,
        LocalContentCache? cache = null,
        IInstallLockProvider? lockProvider = null,
        ApplyPreconditions? preconditions = null,
        IReadOnlyList<IReadableObjectStore>? mirrors = null,
        IReadOnlySet<ContentHash>? protectedCacheEntries = null,
        IApplyTicketProvider? ticketProvider = null)
    {
        progress ??= new NullApplyProgressSink();
        ValidateRoot(installRoot);
        preconditions ??= new ApplyPreconditions();
        preconditions.Validate(installRoot, plan);
        lockProvider ??= new FileInstallLockProvider();
        await using var installLease = await lockProvider.AcquireAsync(installRoot, cancellationToken).ConfigureAwait(false);
        ticketProvider ??= new NoopApplyTicketProvider();
        IApplyTicketLease? ticketLease = null;

        try
        {
        var metadata = Path.Combine(installRoot, ".4sup");
        if (ticketProvider is IRecoverableApplyTicketProvider recoverableTickets)
            await recoverableTickets.RecoverAsync(installRoot, cancellationToken).ConfigureAwait(false);
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
        // Persist the complete plan before stopping services or processes.  A crash after this
        // point leaves a durable recovery point that the next invocation can adopt.
        ticketLease = await ticketProvider.AcquireAsync(installRoot, plan.Operations, cancellationToken).ConfigureAwait(false);
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
            var key = layout.Blob(blob.Content); var path = Path.Combine(staging, Convert.ToHexString(blob.Content.Value.Span).ToLowerInvariant());
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
                    ? await _fetcher.FetchFromMirrorsAsync(activeMirrors, key, blob.Content, path, token, mirror => demotedMirrors.TryAdd(mirror, 0)).ConfigureAwait(false)
                    : await _fetcher.FetchAsync(source, key, blob.Content, path, token).ConfigureAwait(false);
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
                    if (SecureInstallOperations.TryEnsureDirectory(installRoot, directory.Path)) break;
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
                        var staged = Path.Combine(staging, Convert.ToHexString(write.Content.Value.Span).ToLowerInvariant());
                        if (!File.Exists(staged)) throw new InvalidDataException($"Verified staging blob for '{write.Path}' is missing.");
                        await VerifyStagedBlobAsync(staged, write.Content, cancellationToken).ConfigureAwait(false);
                        if (new FileInfo(staged).Length != write.Size) throw new InvalidDataException($"Verified blob size for '{write.Path}' does not match the manifest.");
                        if (!SecureInstallOperations.TryReplaceFile(installRoot, write.Path, staged, write.Policy == FileInstallPolicy.Executable, expectedParent))
                        {
                            var temporary = destination! + ".4sup-new-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                            try
                            {
                                File.Copy(staged, temporary, overwrite: false);
                                if (write.Policy == FileInstallPolicy.Executable && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())) TryMakeExecutable(temporary);
                                if (File.Exists(destination!))
                                {
                                    var aside = destination + ".old-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
                                    File.Move(destination!, aside, overwrite: false);
                                    try { File.Move(temporary, destination!, overwrite: false); }
                                    catch
                                    {
                                        if (!File.Exists(destination!) && File.Exists(aside)) File.Move(aside, destination!, overwrite: false);
                                        throw;
                                    }
                                }
                                else File.Move(temporary, destination!, overwrite: false);
                            }
                            finally { if (File.Exists(temporary)) File.Delete(temporary); }
                        }
                    }
                    nextFiles[write.Path] = new InstalledFile(write.Path, write.Content, write.Size, write.Owner, write.Policy, write.Size, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
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
        finally
        {
            // Restoration is cleanup, not part of the cancelled operation; it must still run.
            if (ticketLease is not null)
            {
                try { await ticketLease.RestoreAsync(CancellationToken.None).ConfigureAwait(false); }
                finally { await ticketLease.DisposeAsync().ConfigureAwait(false); }
            }
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
        if (actual.SchemaVersion != expected.SchemaVersion || !string.Equals(actual.FileSetId, expected.FileSetId, StringComparison.Ordinal) || !string.Equals(actual.ReleaseDigest, expected.ReleaseDigest, StringComparison.Ordinal) || !string.Equals(actual.ProductId, expected.ProductId, StringComparison.Ordinal) || !string.Equals(actual.ReleaseId, expected.ReleaseId, StringComparison.Ordinal) || !actual.Blobs.SequenceEqual(expected.Blobs, StringComparer.Ordinal) || actual.BytesToDownload != expected.BytesToDownload || actual.BytesToWrite != expected.BytesToWrite || actual.NetInstallDelta != expected.NetInstallDelta || !actual.Operations.SequenceEqual(expected.Operations) || !actual.ParentIdentities.OrderBy(x => x.Key, StringComparer.Ordinal).SequenceEqual(expected.ParentIdentities.OrderBy(x => x.Key, StringComparer.Ordinal) ) || !string.Equals(actual.RootIdentity, expected.RootIdentity, StringComparison.Ordinal))
            throw new ApplyPreconditionException("An unfinished apply belongs to a different release, file set, or operation plan; recover it before starting another operation.");
    }

    private sealed record RecoveryPlanMarker(int SchemaVersion, string FileSetId, string ReleaseDigest, string ProductId, string ReleaseId, string[] Blobs, RecoveryOperation[] Operations, long BytesToDownload, long BytesToWrite, long NetInstallDelta, DateTimeOffset CreatedAt)
    {
        public Dictionary<string, string> ParentIdentities { get; init; } = new(StringComparer.Ordinal);
        public string? RootIdentity { get; init; }

        public static RecoveryPlanMarker Create(ComposedFileSet target, InstallLock installLock, InstallPlan plan) => new(1, target.FileSetId.ToString(), installLock.ReleaseDigest.ToString(), installLock.ProductId, installLock.ReleaseId, plan.BlobsToFetch.Select(x => x.Content.ToString()).OrderBy(x => x, StringComparer.Ordinal).ToArray(), plan.Operations.Select(RecoveryOperation.From).ToArray(), plan.BytesToDownload, plan.BytesToWrite, plan.NetInstallDelta, DateTimeOffset.UtcNow)
        {
            ParentIdentities = plan.ParentIdentities.ToDictionary(x => x.Key.Value, x => x.Value.Value, StringComparer.Ordinal),
            RootIdentity = plan.RootIdentity?.Value
        };

        public InstallPlan ToInstallPlan() => new()
        {
            Operations = Operations.Select(x => x.ToOperation()).ToImmutableArray(),
            BlobsToFetch = Blobs.Select(x => new BlobLocator(ContentHash.Parse(x))).ToImmutableArray(),
            BytesToDownload = BytesToDownload,
            BytesToWrite = BytesToWrite,
            PeakFreeSpaceRequiredByVolume = ImmutableDictionary<string, long>.Empty,
            NetInstallDelta = NetInstallDelta,
            ParentIdentities = ParentIdentities.Where(x => VirtualPath.TryCreate(x.Key, out _, out _)).ToImmutableDictionary(x => new VirtualPath(x.Key), x => new FileIdentity(x.Value)),
            RootIdentity = RootIdentity is null ? null : new FileIdentity(RootIdentity)
        };
    }

    private sealed record RecoveryOperation(string Kind, string Path, string? Content, long Size, string? Owner, FileInstallPolicy? Policy, string? ObservedContent, string? Reason)
    {
        public static RecoveryOperation From(FileOperation operation) => operation switch
        {
            FileOperation.Write write => new("write", write.Path.Value, write.Content.ToString(), write.Size, write.Owner.Value, write.Policy, null, null),
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
                "write" when ContentHash.TryParse(Content, out var hash) && PackageId.TryCreate(Owner, out var owner) => new FileOperation.Write(path, hash, Size, owner, Policy ?? FileInstallPolicy.Replace),
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
        var path = GetPath(hash); if (!File.Exists(path)) return false;
        await using var stream = File.OpenRead(path);
        if (await ContentHash.ComputeAsync(stream, hash.Algorithm, cancellationToken).ConfigureAwait(false) != hash)
        {
            try { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); } catch (IOException) { }
            return false;
        }
        File.SetLastAccessTimeUtc(path, DateTime.UtcNow); return true;
    }
    public async ValueTask StoreAsync(ContentHash hash, Stream content, CancellationToken cancellationToken = default, IReadOnlySet<ContentHash>? protectedEntries = null)
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
            try { File.SetAttributes(path, FileAttributes.ReadOnly); } catch (Exception) { }
            await EvictAsync(cancellationToken, protectedEntries).ConfigureAwait(false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async ValueTask EvictAsync(CancellationToken cancellationToken, IReadOnlySet<ContentHash>? protectedEntries)
    {
        var files = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Select(x => new FileInfo(x))
            .Where(x => protectedEntries is null || !protectedEntries.Contains(ParseCacheHash(x.Name)))
            .OrderBy(x => x.LastAccessTimeUtc).ToList();
        var total = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Select(x => new FileInfo(x).Length).Sum();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (total <= _sizeCap) break;
            total -= file.Length;
            try { file.IsReadOnly = false; file.Delete(); } catch (IOException) { }
        }
        await Task.CompletedTask;
    }

    private static ContentHash ParseCacheHash(string name) => ContentHash.TryParse("sha256:" + name, out var hash) ? hash : default;
}
