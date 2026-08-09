namespace NFour.AutoUpdater.Client;

/// <summary>Identifies the filesystem kind observed at an install path.</summary>
public enum ObservedKind { /// <summary>A regular file.</summary>
    File, /// <summary>A directory.</summary>
    Directory, /// <summary>A symbolic link or reparse point.</summary>
    Reparse, /// <summary>Another filesystem object kind.</summary>
    Other }
/// <summary>Controls which observed files are content-hashed during planning.</summary>
public enum HashPolicy { /// <summary>Do not hash observed files.</summary>
    Never, /// <summary>Hash files whose metadata differs from the ledger.</summary>
    Changed, /// <summary>Hash every observed file.</summary>
    Always }
/// <summary>Represents stable platform-specific filesystem identity.</summary><param name="Value">The encoded identity.</param>
public readonly record struct FileIdentity(string Value);
/// <summary>Describes one file recorded in the install ledger.</summary><param name="Path">The install path.</param><param name="Content">The managed content digest.</param><param name="Size">The managed content size.</param><param name="Owner">The owning package.</param><param name="Policy">The install policy.</param><param name="ObservedSize">The size recorded after apply.</param><param name="ObservedMtimeUnix">The modification time recorded after apply.</param><param name="State">The management state.</param><param name="ObservedContent">The digest of adopted content.</param><param name="UnknownFields">Preserved extension fields.</param><param name="Mode">The applied POSIX mode.</param>
public sealed record InstalledFile(VirtualPath Path, ContentHash? Content, long Size, PackageId Owner, FileInstallPolicy Policy, long ObservedSize, long ObservedMtimeUnix, string State = "managed", ContentHash? ObservedContent = null, ImmutableDictionary<string, JsonElement>? UnknownFields = null, string? Mode = null);
/// <summary>Describes the current filesystem state at an install path.</summary><param name="Path">The install path.</param><param name="Exists">Whether an entry exists.</param><param name="Kind">The observed entry kind.</param><param name="Size">The observed size.</param><param name="MtimeUnixSeconds">The observed modification time.</param><param name="Identity">The filesystem identity.</param><param name="Hash">The optional observed content hash.</param>
public sealed record ObservedEntry(VirtualPath Path, bool Exists, ObservedKind Kind, long Size, long MtimeUnixSeconds, FileIdentity? Identity, ContentHash? Hash);
/// <summary>Contains observations made beneath one install root.</summary><param name="InstallRoot">The absolute install root.</param><param name="Entries">Observed entries keyed by path.</param>
public sealed record ObservedTreeSnapshot(string InstallRoot, ImmutableDictionary<VirtualPath, ObservedEntry> Entries);

/// <summary>Represents one deterministic filesystem action in an install plan.</summary><param name="Path">The affected install path.</param>
public abstract record FileOperation(VirtualPath Path)
{
    /// <summary>Writes verified content to a managed file.</summary>
    /// <param name="Path">The install path.</param>
    /// <param name="Content">The required content digest.</param>
    /// <param name="Size">The required byte length.</param>
    /// <param name="Owner">The owning package.</param>
    /// <param name="Policy">The install policy.</param>
    /// <param name="Mode">
    /// Manifest-declared POSIX mode, or null to use the policy default. Carried on the
    /// operation rather than re-derived at apply time so that a plan, its recovery marker and
    /// the ledger all describe the same intended permissions.
    /// </param>
    public sealed record Write(VirtualPath Path, ContentHash Content, long Size, PackageId Owner, FileInstallPolicy Policy, string? Mode = null) : FileOperation(Path);
    /// <summary>Deletes a previously managed path.</summary><param name="Path">The install path.</param><param name="PreviousOwner">The previous owning package.</param>
    public sealed record Delete(VirtualPath Path, PackageId PreviousOwner) : FileOperation(Path);
    /// <summary>Keeps the existing entry unchanged.</summary><param name="Path">The install path.</param>
    public sealed record Keep(VirtualPath Path) : FileOperation(Path);
    /// <summary>Leaves a removed package's protected content unmanaged.</summary><param name="Path">The install path.</param><param name="PreviousOwner">The previous owning package.</param><param name="Reason">Why the path is retained.</param>
    public sealed record Orphan(VirtualPath Path, PackageId PreviousOwner, string Reason) : FileOperation(Path);
    /// <summary>Adopts existing content into ledger ownership.</summary><param name="Path">The install path.</param><param name="Owner">The new owning package.</param><param name="Policy">The install policy.</param><param name="ObservedContent">The observed content digest.</param>
    public sealed record Adopt(VirtualPath Path, PackageId Owner, FileInstallPolicy Policy, ContentHash? ObservedContent) : FileOperation(Path);
    /// <summary>Ensures a directory exists.</summary><param name="Path">The install path.</param>
    public sealed record EnsureDirectory(VirtualPath Path) : FileOperation(Path);
}

/// <summary>Contains ordered filesystem operations and their resource requirements.</summary>
public sealed record InstallPlan
{
    /// <summary>Gets ordered filesystem operations.</summary>
    public required ImmutableArray<FileOperation> Operations { get; init; }
    /// <summary>Gets unique blobs that must be fetched.</summary>
    public required ImmutableArray<BlobLocator> BlobsToFetch { get; init; }
    /// <summary>Gets total download bytes.</summary>
    public required long BytesToDownload { get; init; }
    /// <summary>Gets total bytes written into the install.</summary>
    public required long BytesToWrite { get; init; }
    /// <summary>Gets peak free-space requirements keyed by volume.</summary>
    public required ImmutableDictionary<string, long> PeakFreeSpaceRequiredByVolume { get; init; }
    /// <summary>Gets the net installed-size change.</summary>
    public required long NetInstallDelta { get; init; }
    /// <summary>Gets parent directory identities captured while planning.</summary>
    public ImmutableDictionary<VirtualPath, FileIdentity> ParentIdentities { get; init; } = ImmutableDictionary<VirtualPath, FileIdentity>.Empty;
    /// <summary>Gets the captured install-root identity.</summary>
    public FileIdentity? RootIdentity { get; init; }
}

/// <summary>Scans relevant install paths without following links.</summary>
public interface ITreeScanner
{
    /// <summary>Scans ledger and target paths beneath an install root.</summary>
    /// <param name="installRoot">The absolute install root.</param>
    /// <param name="ledgerPaths">Paths present in the current ledger.</param>
    /// <param name="targetPaths">Paths present in the target file set.</param>
    /// <param name="hashPolicy">The observed-content hash policy.</param>
    /// <param name="ledger">
    /// Previously recorded observations, used by <see cref="HashPolicy.Changed"/> to decide
    /// which files actually need re-hashing. Without it, Changed degrades to Always.
    /// </param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    ValueTask<ObservedTreeSnapshot> ScanAsync(string installRoot, IEnumerable<VirtualPath> ledgerPaths, IEnumerable<VirtualPath> targetPaths, HashPolicy hashPolicy, IReadOnlyDictionary<VirtualPath, InstalledFile>? ledger = null, CancellationToken cancellationToken = default);
}
/// <summary>Builds a deterministic install plan from target and observed state.</summary>
public interface IInstallPlanner
{
    /// <summary>Plans the operations required to reach a composed target.</summary>
    InstallPlan Plan(ComposedFileSet target, IReadOnlyDictionary<VirtualPath, InstalledFile>? current, ObservedTreeSnapshot observed);
}

/// <summary>Builds deterministic, resource-accounted install plans.</summary>
public sealed class InstallPlanner : IInstallPlanner
{
    /// <inheritdoc />
    public InstallPlan Plan(ComposedFileSet target, IReadOnlyDictionary<VirtualPath, InstalledFile>? current, ObservedTreeSnapshot observed)
    {
        var operations = ImmutableArray.CreateBuilder<FileOperation>();
        var blobs = new Dictionary<ContentHash, BlobLocator>();
        long writeBytes = 0;
        foreach (var (path, want) in target.Files)
        {
            observed.Entries.TryGetValue(path, out var observation);
            if (current is null || !current.TryGetValue(path, out var have))
            {
                if (want.Kind == FileEntryKind.Directory) { operations.Add(new FileOperation.EnsureDirectory(path)); continue; }
                if (want.Policy == FileInstallPolicy.Preserve && observation is { Exists: true, Kind: ObservedKind.File }) operations.Add(new FileOperation.Adopt(path, want.Owner, want.Policy, observation.Hash));
                else { operations.Add(new FileOperation.Write(path, want.Content, want.Size, want.Owner, want.Policy, want.Mode)); AddBlob(want.Content, want.Size); }
                continue;
            }
            // A missing observed hash is absence of evidence, not evidence of a match. Under
            // HashPolicy.Never (and for files Changed skipped) nothing was hashed at all, so
            // treating null as "matches" meant a locally modified file was kept purely because
            // the *ledger's* hash still matched the target. The explicit policy is to trust the
            // ledger's own record of what was written, and to detect local modification by
            // comparing the file's current size and mtime against that record.
            var observedMatches = observation is { Exists: true, Kind: ObservedKind.File }
                && (observation.Hash is { } observedHash
                    ? observedHash == want.Content
                    : have.ObservedSize == observation.Size && have.ObservedMtimeUnix == observation.MtimeUnixSeconds);
            if (want.Kind == FileEntryKind.Directory)
            {
                if (observation is { Exists: true, Kind: ObservedKind.Directory }) operations.Add(new FileOperation.Keep(path));
                else operations.Add(new FileOperation.EnsureDirectory(path));
            }
            else if (have.Content == want.Content && have.State != "adopted" && observedMatches) operations.Add(new FileOperation.Keep(path));
            else if (want.Policy == FileInstallPolicy.Preserve && observation is { Exists: true, Kind: ObservedKind.File }) operations.Add(new FileOperation.Keep(path));
            else { operations.Add(new FileOperation.Write(path, want.Content, want.Size, want.Owner, want.Policy, want.Mode)); AddBlob(want.Content, want.Size); }
        }
        if (current is not null)
            foreach (var (path, had) in current)
            {
                if (target.Files.ContainsKey(path)) continue;
                if (had.Policy == FileInstallPolicy.Preserve || had.State == "adopted") operations.Add(new FileOperation.Orphan(path, had.Owner, "Preserve policy protects unmanaged content."));
                else operations.Add(new FileOperation.Delete(path, had.Owner));
            }
        var stagingBytes = blobs.Sum(x => target.Files.Values.First(y => y.Content == x.Key).Size);
        // Staging remains present until the ledger commit completes.  Account for every
        // materialised write at the same time; using only the largest write understated the
        // required free space for a multi-file update and could fail halfway through apply.
        var peakBytes = stagingBytes + operations.OfType<FileOperation.Write>().Sum(x => x.Size);
        var volume = Path.GetPathRoot(Path.GetFullPath(observed.InstallRoot)) ?? observed.InstallRoot;
        var peak = ImmutableDictionary<string, long>.Empty.SetItem(volume, peakBytes);
        var removedBytes = current is null ? 0 : current.Where(x => operations.Any(op => op is FileOperation.Delete delete && delete.Path == x.Key)).Sum(x => x.Value.Size);
        var net = operations.OfType<FileOperation.Write>().Sum(x => x.Size) - removedBytes;
        var parents = ImmutableDictionary.CreateBuilder<VirtualPath, FileIdentity>();
        foreach (var operation in operations)
        {
            var separator = operation.Path.Value.LastIndexOf('/');
            if (separator <= 0) continue;
            var parent = new VirtualPath(operation.Path.Value[..separator]);
            if (observed.Entries.TryGetValue(parent, out var parentObservation) && parentObservation.Identity is { } identity) parents[parent] = identity;
        }
        FileIdentity? rootIdentity = FileIdentityProvider.TryGet(observed.InstallRoot, out var observedRoot) ? observedRoot : null;
        return new InstallPlan { Operations = operations.ToImmutable(), BlobsToFetch = blobs.Values.ToImmutableArray(), BytesToDownload = stagingBytes, BytesToWrite = writeBytes == 0 ? operations.OfType<FileOperation.Write>().Sum(x => x.Size) : writeBytes, PeakFreeSpaceRequiredByVolume = peak, NetInstallDelta = net, ParentIdentities = parents.ToImmutable(), RootIdentity = rootIdentity };

        void AddBlob(ContentHash content, long size) { if (!blobs.ContainsKey(content)) blobs[content] = new BlobLocator(content, size); }
    }

}

/// <summary>Scans local install entries while rejecting traversal through reparse points.</summary>
public sealed class LocalTreeScanner : ITreeScanner
{
    /// <inheritdoc />
    public async ValueTask<ObservedTreeSnapshot> ScanAsync(string installRoot, IEnumerable<VirtualPath> ledgerPaths, IEnumerable<VirtualPath> targetPaths, HashPolicy hashPolicy, IReadOnlyDictionary<VirtualPath, InstalledFile>? ledger = null, CancellationToken cancellationToken = default)
    {
        var entries = ImmutableDictionary.CreateBuilder<VirtualPath, ObservedEntry>();
        var requested = new HashSet<VirtualPath>(ledgerPaths.Concat(targetPaths));
        foreach (var path in requested.ToArray())
        {
            var components = path.Value.Split('/');
            for (var count = 1; count < components.Length; count++) requested.Add(new VirtualPath(string.Join('/', components.Take(count))));
        }
        foreach (var path in requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = Resolve(installRoot, path);
            var info = new FileInfo(full);
            if (Directory.Exists(full))
            {
                var directory = new DirectoryInfo(full);
                var isReparse = directory.Attributes.HasFlag(FileAttributes.ReparsePoint);
                entries[path] = new(path, true, isReparse ? ObservedKind.Reparse : ObservedKind.Directory, 0, new DateTimeOffset(directory.LastWriteTimeUtc).ToUnixTimeSeconds(), FileIdentityProvider.TryGet(full, out var directoryIdentity) ? directoryIdentity : null, null);
            }
            else if (info.Exists)
            {
                // Changed means "hash only what the ledger says might have moved". Hashing
                // every file the way Always does defeats the point of the policy on a large
                // install; a file whose size and mtime still match what was recorded when it
                // was written is left unhashed, and the planner falls back to that same
                // ledger evidence to decide whether it still matches.
                var unchangedSinceLedger = ledger is not null
                    && ledger.TryGetValue(path, out var recorded)
                    && recorded.ObservedSize == info.Length
                    && recorded.ObservedMtimeUnix == new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
                var needsHash = hashPolicy switch
                {
                    HashPolicy.Always => true,
                    HashPolicy.Changed => !unchangedSinceLedger,
                    _ => false
                };

                ContentHash? hash = null;
                if (needsHash) await using (var stream = info.OpenRead()) hash = await ContentHash.ComputeAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                entries[path] = new(path, true, info.Attributes.HasFlag(FileAttributes.ReparsePoint) ? ObservedKind.Reparse : ObservedKind.File, info.Length, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(), FileIdentityProvider.TryGet(full, out var fileIdentity) ? fileIdentity : new FileIdentity($"{info.Length}:{info.LastWriteTimeUtc.Ticks}"), hash);
            }
            else entries[path] = new(path, false, ObservedKind.Other, 0, 0, null, null);
        }
        return new ObservedTreeSnapshot(installRoot, entries.ToImmutable());
    }
    private static string Resolve(string root, VirtualPath path) { var full = Path.GetFullPath(Path.Combine(root, path.Value.Replace('/', Path.DirectorySeparatorChar))); if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new IOException("Path escapes install root."); return full; }
}
