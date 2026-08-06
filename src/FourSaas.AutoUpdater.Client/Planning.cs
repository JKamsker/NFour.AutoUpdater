namespace FourSaas.AutoUpdater.Client;

public enum ObservedKind { File, Directory, Reparse, Other }
public enum HashPolicy { Never, Changed, Always }
public readonly record struct FileIdentity(string Value);
public sealed record InstalledFile(VirtualPath Path, ContentHash? Content, long Size, PackageId Owner, FileInstallPolicy Policy, long ObservedSize, long ObservedMtimeUnix, string State = "managed", ContentHash? ObservedContent = null);
public sealed record ObservedEntry(VirtualPath Path, bool Exists, ObservedKind Kind, long Size, long MtimeUnixSeconds, FileIdentity? Identity, ContentHash? Hash);
public sealed record ObservedTreeSnapshot(string InstallRoot, ImmutableDictionary<VirtualPath, ObservedEntry> Entries);

public abstract record FileOperation(VirtualPath Path)
{
    public sealed record Write(VirtualPath Path, ContentHash Content, long Size, PackageId Owner, FileInstallPolicy Policy) : FileOperation(Path);
    public sealed record Delete(VirtualPath Path, PackageId PreviousOwner) : FileOperation(Path);
    public sealed record Keep(VirtualPath Path) : FileOperation(Path);
    public sealed record Orphan(VirtualPath Path, PackageId PreviousOwner, string Reason) : FileOperation(Path);
    public sealed record Adopt(VirtualPath Path, PackageId Owner, FileInstallPolicy Policy, ContentHash? ObservedContent) : FileOperation(Path);
    public sealed record EnsureDirectory(VirtualPath Path) : FileOperation(Path);
}

public sealed record InstallPlan
{
    public required ImmutableArray<FileOperation> Operations { get; init; }
    public required ImmutableArray<BlobLocator> BlobsToFetch { get; init; }
    public required long BytesToDownload { get; init; }
    public required long BytesToWrite { get; init; }
    public required ImmutableDictionary<string, long> PeakFreeSpaceRequiredByVolume { get; init; }
    public required long NetInstallDelta { get; init; }
    public ImmutableDictionary<VirtualPath, FileIdentity> ParentIdentities { get; init; } = ImmutableDictionary<VirtualPath, FileIdentity>.Empty;
    public FileIdentity? RootIdentity { get; init; }
}

public interface ITreeScanner
{
    ValueTask<ObservedTreeSnapshot> ScanAsync(string installRoot, IEnumerable<VirtualPath> ledgerPaths, IEnumerable<VirtualPath> targetPaths, HashPolicy hashPolicy, CancellationToken cancellationToken = default);
}
public interface IInstallPlanner
{
    InstallPlan Plan(ComposedFileSet target, IReadOnlyDictionary<VirtualPath, InstalledFile>? current, ObservedTreeSnapshot observed);
}

public sealed class InstallPlanner : IInstallPlanner
{
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
                else { operations.Add(new FileOperation.Write(path, want.Content, want.Size, want.Owner, want.Policy)); AddBlob(want.Content, want.Size); }
                continue;
            }
            var observedMatches = observation is { Exists: true, Kind: ObservedKind.File } && (observation.Hash is null || observation.Hash == want.Content);
            if (want.Kind == FileEntryKind.Directory)
            {
                if (observation is { Exists: true, Kind: ObservedKind.Directory }) operations.Add(new FileOperation.Keep(path));
                else operations.Add(new FileOperation.EnsureDirectory(path));
            }
            else if (have.Content == want.Content && have.State != "adopted" && observedMatches) operations.Add(new FileOperation.Keep(path));
            else if (want.Policy == FileInstallPolicy.Preserve && observation is { Exists: true, Kind: ObservedKind.File }) operations.Add(new FileOperation.Keep(path));
            else { operations.Add(new FileOperation.Write(path, want.Content, want.Size, want.Owner, want.Policy)); AddBlob(want.Content, want.Size); }
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

        void AddBlob(ContentHash content, long size) { if (!blobs.ContainsKey(content)) blobs[content] = new BlobLocator(content); }
    }

}

public sealed class LocalTreeScanner : ITreeScanner
{
    public async ValueTask<ObservedTreeSnapshot> ScanAsync(string installRoot, IEnumerable<VirtualPath> ledgerPaths, IEnumerable<VirtualPath> targetPaths, HashPolicy hashPolicy, CancellationToken cancellationToken = default)
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
                ContentHash? hash = null;
                if (hashPolicy != HashPolicy.Never) await using (var stream = info.OpenRead()) hash = await ContentHash.ComputeAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                entries[path] = new(path, true, info.Attributes.HasFlag(FileAttributes.ReparsePoint) ? ObservedKind.Reparse : ObservedKind.File, info.Length, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(), FileIdentityProvider.TryGet(full, out var fileIdentity) ? fileIdentity : new FileIdentity($"{info.Length}:{info.LastWriteTimeUtc.Ticks}"), hash);
            }
            else entries[path] = new(path, false, ObservedKind.Other, 0, 0, null, null);
        }
        return new ObservedTreeSnapshot(installRoot, entries.ToImmutable());
    }
    private static string Resolve(string root, VirtualPath path) { var full = Path.GetFullPath(Path.Combine(root, path.Value.Replace('/', Path.DirectorySeparatorChar))); if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new IOException("Path escapes install root."); return full; }
}
