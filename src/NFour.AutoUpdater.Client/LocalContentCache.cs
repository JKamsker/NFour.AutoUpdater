using System.Security.Cryptography;

namespace NFour.AutoUpdater.Client;

/// <summary>Stores verified blobs in a size-bounded local content cache.</summary>
public sealed class LocalContentCache
{
    private const long DefaultSizeCapBytes = 20L * 1024 * 1024 * 1024;
    private readonly string _root;
    private readonly long _sizeCap;
    /// <summary>Initializes a local cache beneath a root directory.</summary>
    public LocalContentCache(string root, long sizeCap = DefaultSizeCapBytes) { _root = root; _sizeCap = sizeCap; Directory.CreateDirectory(_root); }
    /// <summary>Gets the deterministic cache path for a content hash.</summary>
    public string GetPath(ContentHash hash) => Path.Combine(_root, hash.Algorithm.ToString().ToLowerInvariant(), hash.ToString().Split(':')[1][..2], hash.ToString().Split(':')[1][2..4], hash.ToString().Split(':')[1]);
    /// <summary>Determines whether a verified cache entry exists.</summary>
    public async ValueTask<bool> TryGetAsync(ContentHash hash, CancellationToken cancellationToken = default)
    {
        var path = GetPath(hash); if (!File.Exists(path)) return false;

        // The read handle is scoped to the hash computation and closed before the eviction
        // below. File.OpenRead takes FileShare.Read, which does not permit deletion, so
        // holding it across File.Delete leaves tampered content in the cache: the mismatch is
        // detected, the delete quietly fails, and the next caller re-reads the same bad bytes.
        bool matches;
        await using (var stream = File.OpenRead(path))
            matches = await ContentHash.ComputeAsync(stream, hash.Algorithm, cancellationToken).ConfigureAwait(false) == hash;

        if (!matches)
        {
            try { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return false;
        }
        File.SetLastAccessTimeUtc(path, DateTime.UtcNow); return true;
    }
    /// <summary>Stores a verified cache entry and enforces the size cap.</summary>
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

