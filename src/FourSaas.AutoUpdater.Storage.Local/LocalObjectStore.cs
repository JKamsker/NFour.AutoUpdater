using FourSaas.AutoUpdater.Core;
using FourSaas.AutoUpdater.Storage;

namespace FourSaas.AutoUpdater.Storage.Local;

public sealed class LocalObjectStore : IListableObjectStore, IConditionalWriteStore, IServerSideCopyStore, IServerSideVerifier
{
    private readonly string _root;
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);
    public LocalObjectStore(string root) => _root = Path.GetFullPath(root);
    public StorageCapabilities Capabilities => StorageCapabilities.Read | StorageCapabilities.Range | StorageCapabilities.List | StorageCapabilities.Write | StorageCapabilities.ConditionalWrite | StorageCapabilities.ServerSideCopy | StorageCapabilities.Delete;
    public int RecommendedParallelism => 16;

    public ValueTask<ReadResult?> OpenAsync(ObjectKey key, long offset = 0, ObjectValidator? ifMatch = null, CancellationToken cancellationToken = default)
    {
        var path = Resolve(key);
        if (!File.Exists(path)) return ValueTask.FromResult<ReadResult?>(null);
        var info = new FileInfo(path);
        var validator = Validator(info);
        if (ifMatch is not null && ifMatch.Value != validator.Value)
        {
            var restart = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return ValueTask.FromResult<ReadResult?>(new ReadResult { Content = restart, ActualStartOffset = 0, StatusCode = 200, Validator = validator });
        }
        if (offset < 0 || offset > info.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.Position = offset;
        return ValueTask.FromResult<ReadResult?>(new ReadResult { Content = stream, ActualStartOffset = offset, StatusCode = offset == 0 ? 200 : 206, Validator = validator });
    }
    public ValueTask<ObjectHead?> HeadAsync(ObjectKey key, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(Resolve(key));
        return ValueTask.FromResult(info.Exists ? new ObjectHead(info.Length, Validator(info), LastModified: info.LastWriteTimeUtc) : null);
    }
    public async IAsyncEnumerable<ObjectKey> ListAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_root)) yield break;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).OrderBy(static x => x, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (prefix is null || key.StartsWith(prefix, StringComparison.Ordinal)) yield return new ObjectKey(key);
            await Task.Yield();
        }
    }
    public async ValueTask PutAsync(ObjectKey key, Stream content, long? length = null, CancellationToken cancellationToken = default)
    {
        var destination = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            {
                await content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public ValueTask DeleteAsync(ObjectKey key, CancellationToken cancellationToken = default) { var path = Resolve(key); if (File.Exists(path)) File.Delete(path); return ValueTask.CompletedTask; }
    public ValueTask<bool> PutIfAbsentAsync(ObjectKey key, Stream content, long? length = null, CancellationToken cancellationToken = default)
    {
        var gate = _locks.GetOrAdd(key.Value, static _ => new object());
        lock (gate)
        {
            var destination = Resolve(key); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination)) return ValueTask.FromResult(false);
            try
            {
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: false);
                content.CopyTo(output); output.Flush(flushToDisk: true); return ValueTask.FromResult(true);
            }
            catch (IOException) when (File.Exists(destination)) { return ValueTask.FromResult(false); }
        }
    }
    public ValueTask<bool> CompareAndSwapAsync(ObjectKey key, ObjectValidator expected, Stream content, long? length = null, CancellationToken cancellationToken = default)
    {
        var gate = _locks.GetOrAdd(key.Value, static _ => new object());
        lock (gate)
        {
            var info = new FileInfo(Resolve(key));
            if (!info.Exists || Validator(info).Value != expected.Value) return ValueTask.FromResult(false);
            using var output = new FileStream(Resolve(key) + ".cas-tmp", FileMode.Create, FileAccess.Write, FileShare.None);
            content.CopyTo(output); output.Flush(flushToDisk: true); output.Dispose(); File.Move(Resolve(key) + ".cas-tmp", Resolve(key), overwrite: true); return ValueTask.FromResult(true);
        }
    }
    public ValueTask CopyAsync(ObjectKey source, ObjectKey destination, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        var sourcePath = Resolve(source); var destinationPath = Resolve(destination); Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath, overwrite); return ValueTask.CompletedTask;
    }
    public async ValueTask<bool> VerifyAsync(ObjectKey key, ContentHash expected, CancellationToken cancellationToken = default)
    {
        var path = Resolve(key); if (!File.Exists(path)) return false;
        await using var stream = File.OpenRead(path);
        return await ContentHash.ComputeAsync(stream, expected.Algorithm, cancellationToken).ConfigureAwait(false) == expected;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private string Resolve(ObjectKey key)
    {
        var combined = Path.GetFullPath(Path.Combine(_root, key.Value.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !string.Equals(combined, _root, StringComparison.Ordinal)) throw new IOException("Object key escapes the store root.");
        return combined;
    }
    private static ObjectValidator Validator(FileInfo info) => new(ObjectValidatorKind.SizeAndMtime, $"{info.Length}:{info.LastWriteTimeUtc.Ticks}", false);
}
