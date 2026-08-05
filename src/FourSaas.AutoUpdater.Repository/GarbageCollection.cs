namespace FourSaas.AutoUpdater.Repository;

public sealed record GarbageCollectionOptions
{
    public TimeSpan MinimumBlobAge { get; init; } = TimeSpan.FromHours(24);
    public bool DryRun { get; init; }
    public string QuarantinePrefix { get; init; } = "_trash";
}
public sealed record GarbageCollectionResult(ImmutableArray<ObjectKey> Marked, ImmutableArray<ObjectKey> Quarantined, ImmutableArray<ObjectKey> Deleted, ImmutableArray<Diagnostic> Diagnostics);

public sealed class GarbageCollector
{
    public async ValueTask<GarbageCollectionResult> CollectAsync(IListableObjectStore store, RepositoryLayout layout, IEnumerable<ContentHash> liveHashes, GarbageCollectionOptions? options = null, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        options ??= new(); timeProvider ??= TimeProvider.System;
        var marked = liveHashes.Select(layout.Blob).ToImmutableHashSet();
        var writable = store as IWritableObjectStore;
        var candidates = ImmutableArray.CreateBuilder<ObjectKey>(); var quarantined = ImmutableArray.CreateBuilder<ObjectKey>(); var deleted = ImmutableArray.CreateBuilder<ObjectKey>(); var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        await foreach (var key in store.ListAsync("blobs/", cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (marked.Contains(key)) continue;
            var head = await store.HeadAsync(key, cancellationToken).ConfigureAwait(false);
            if (head is null) continue;
            if (head.LastModified is null)
            {
                diagnostics.Add(new("GC002", DiagnosticSeverity.Warning, $"Blob '{key}' has no reliable age; it was retained."));
                continue;
            }
            if (head.LastModified > timeProvider.GetUtcNow() - options.MinimumBlobAge) continue;
            candidates.Add(key);
            if (options.DryRun) continue;
            var trash = new ObjectKey($"{options.QuarantinePrefix}/{timeProvider.GetUtcNow():yyyyMMdd}/{Guid.NewGuid():N}/{Path.GetFileName(key.Value)}");
            if (writable is null) { diagnostics.Add(new("GC001", DiagnosticSeverity.Error, "Garbage collection requires a writable authoritative store.")); continue; }
            if (writable is IServerSideCopyStore copy)
            {
                await copy.CopyAsync(key, trash, overwrite: false, cancellationToken).ConfigureAwait(false);
                await writable.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
                quarantined.Add(trash);
            }
            else await writable.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            deleted.Add(key);
        }
        return new GarbageCollectionResult(marked.ToImmutableArray(), quarantined.ToImmutable(), deleted.ToImmutable(), diagnostics.ToImmutable());
    }

    public static ImmutableHashSet<ContentHash> MarkRelease(ReleaseLock release, IEnumerable<PackageManifest> manifests)
    {
        var marked = ImmutableHashSet.CreateBuilder<ContentHash>();
        marked.Add(release.CoverageDigest);
        foreach (var pin in release.Packages)
        {
            marked.Add(pin.ManifestDigest);
            var manifest = manifests.FirstOrDefault(x => x.Id == pin.Id && x.Version.Label == pin.Version.Label);
            if (manifest is null) continue;
            marked.Add(manifest.FileTable.Digest);
            foreach (var shard in manifest.FileTable.Shards) marked.Add(shard.Digest);
        }
        return marked.ToImmutable();
    }

    public static async ValueTask<ImmutableHashSet<ContentHash>> MarkReleaseAsync(ReleaseLock release, IPackageRepository repository, CancellationToken cancellationToken = default)
    {
        var marked = ImmutableHashSet.CreateBuilder<ContentHash>();
        marked.Add(release.CoverageDigest);
        foreach (var pin in release.Packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            marked.Add(pin.ManifestDigest);
            var manifest = await repository.GetManifestAsync(pin.Id, pin.Version, cancellationToken).ConfigureAwait(false);
            if (manifest is null) continue;
            marked.Add(manifest.FileTable.Digest);
            foreach (var shard in manifest.FileTable.Shards) marked.Add(shard.Digest);
            await foreach (var entry in repository.ReadFileTableAsync(manifest, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                if (entry.Kind == FileEntryKind.File) marked.Add(entry.Content);
        }
        return marked.ToImmutable();
    }
}
