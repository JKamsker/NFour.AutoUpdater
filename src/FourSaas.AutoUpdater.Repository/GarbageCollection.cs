namespace FourSaas.AutoUpdater.Repository;

public sealed record GarbageCollectionOptions
{
    public TimeSpan MinimumBlobAge { get; init; } = TimeSpan.FromHours(24);
    public bool DryRun { get; init; }
    public bool IncludeStaging { get; init; }
    public string QuarantinePrefix { get; init; } = "_trash";
}
public sealed record GarbageCollectionResult(ImmutableArray<ObjectKey> Marked, ImmutableArray<ObjectKey> Quarantined, ImmutableArray<ObjectKey> Deleted, ImmutableArray<Diagnostic> Diagnostics);

public sealed class GarbageCollector
{
    public async ValueTask<GarbageCollectionResult> CollectLiveAsync(IListableObjectStore store, RepositoryLayout layout, IEnumerable<ReleaseLock> liveReleases, IPackageRepository repository, GarbageCollectionOptions? options = null, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        var live = ImmutableHashSet.CreateBuilder<ContentHash>();
        foreach (var release in liveReleases)
            live.UnionWith(await MarkReleaseAsync(release, repository, cancellationToken).ConfigureAwait(false));
        return await CollectAsync(store, layout, live, options, timeProvider, cancellationToken).ConfigureAwait(false);
    }

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
            if (options.DryRun) { deleted.Add(key); continue; }
            var trash = new ObjectKey($"{options.QuarantinePrefix}/{timeProvider.GetUtcNow():yyyyMMdd}/{Guid.NewGuid():N}/{Path.GetFileName(key.Value)}");
            if (writable is null) { diagnostics.Add(new("GC001", DiagnosticSeverity.Error, "Garbage collection requires a writable authoritative store.")); continue; }
            await QuarantineAsync(writable, key, trash, head.Length, cancellationToken).ConfigureAwait(false);
            await writable.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            quarantined.Add(trash);
            deleted.Add(key);
        }
        if (options.IncludeStaging)
        {
            await foreach (var key in store.ListAsync("_staging/", cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var head = await store.HeadAsync(key, cancellationToken).ConfigureAwait(false);
                if (head?.LastModified is null || head.LastModified > timeProvider.GetUtcNow() - options.MinimumBlobAge) continue;
                if (options.DryRun) { deleted.Add(key); continue; }
                if (writable is null) { diagnostics.Add(new("GC001", DiagnosticSeverity.Error, "Garbage collection requires a writable authoritative store.")); continue; }
                var trash = new ObjectKey($"{options.QuarantinePrefix}/{timeProvider.GetUtcNow():yyyyMMdd}/staging/{Guid.NewGuid():N}/{Path.GetFileName(key.Value)}");
                await QuarantineAsync(writable, key, trash, head.Length, cancellationToken).ConfigureAwait(false);
                await writable.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
                quarantined.Add(trash);
                deleted.Add(key);
            }
        }
        return new GarbageCollectionResult(marked.ToImmutableArray(), quarantined.ToImmutable(), deleted.ToImmutable(), diagnostics.ToImmutable());
    }

    public static ImmutableHashSet<ContentHash> MarkRelease(ReleaseLock release, IEnumerable<PackageManifest> manifests, IReadOnlyDictionary<PackageId, IEnumerable<PackageFileEntry>>? fileEntries = null)
    {
        var marked = ImmutableHashSet.CreateBuilder<ContentHash>();
        marked.Add(release.CoverageDigest);
        foreach (var pin in release.Packages)
        {
            marked.Add(pin.ManifestDigest);
            var manifest = manifests.FirstOrDefault(x => x.Id == pin.Id && x.Version.Label == pin.Version.Label);
            if (manifest is null) throw new InvalidDataException($"Missing manifest for live package '{pin.Id}@{pin.Version}'.");
            VerifyManifestPin(pin, manifest);
            marked.Add(manifest.FileTable.Digest);
            foreach (var shard in manifest.FileTable.Shards) marked.Add(shard.Digest);
            if (fileEntries is not null && fileEntries.TryGetValue(pin.Id, out var entries))
                foreach (var entry in entries)
                    if (entry.Kind == FileEntryKind.File) marked.Add(entry.Content);
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
            var manifest = repository is IManifestDigestRepository digestRepository
                ? await digestRepository.GetManifestAsync(pin.Id, pin.Version, pin.ManifestDigest, cancellationToken).ConfigureAwait(false)
                : await repository.GetManifestAsync(pin.Id, pin.Version, cancellationToken).ConfigureAwait(false);
            if (manifest is null) throw new InvalidDataException($"Missing manifest for live package '{pin.Id}@{pin.Version}'.");
            VerifyManifestPin(pin, manifest);
            marked.Add(manifest.FileTable.Digest);
            foreach (var shard in manifest.FileTable.Shards) marked.Add(shard.Digest);
            await foreach (var entry in repository.ReadFileTableAsync(manifest, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                if (entry.Kind == FileEntryKind.File) marked.Add(entry.Content);
        }
        return marked.ToImmutable();
    }

    private static void VerifyManifestPin(LockedPackage pin, PackageManifest manifest)
    {
        if (manifest.SchemaVersion != 1) throw new FormatException($"Unsupported package schemaVersion {manifest.SchemaVersion} for '{pin.Id}@{pin.Version}'.");
        if (manifest.Id != pin.Id || !string.Equals(manifest.Version.Label, pin.Version.Label, StringComparison.Ordinal) || manifest.Version.Sequence != pin.Version.Sequence)
            throw new InvalidDataException($"Manifest identity does not match live package '{pin.Id}@{pin.Version}'.");
        if (ContentHash.Compute(RepositoryJson.SerializeManifest(manifest)) != pin.ManifestDigest)
            throw new CryptographicException($"Manifest '{pin.Id}@{pin.Version}' failed its pinned digest.");
    }

    private static async ValueTask QuarantineAsync(IWritableObjectStore store, ObjectKey source, ObjectKey destination, long length, CancellationToken cancellationToken)
    {
        if (store is IServerSideCopyStore copy)
        {
            await copy.CopyAsync(source, destination, overwrite: false, cancellationToken).ConfigureAwait(false);
            var parts = source.Value.Split('/');
            if (store is IServerSideVerifier verifier && parts.Length > 1 && ContentHash.TryParse(parts[1] + ":" + Path.GetFileName(source.Value), out var expected) && !await verifier.VerifyAsync(destination, expected, cancellationToken).ConfigureAwait(false))
                throw new CryptographicException($"Quarantined object '{source}' failed verification.");
            return;
        }

        var result = await store.OpenAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException(source.Value);
        await using (result.ConfigureAwait(false))
            await store.PutAsync(destination, result.Content, length, cancellationToken).ConfigureAwait(false);
    }
}
