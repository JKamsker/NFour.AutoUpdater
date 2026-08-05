using FourSaas.AutoUpdater.Core;
using FourSaas.AutoUpdater.Storage;

namespace FourSaas.AutoUpdater.Storage.Local;

public sealed class LocalObjectStore : IListableObjectStore, IConditionalWriteStore, IContentAddressedWriteStore, IServerSideCopyStore, IServerSideVerifier
{
    private readonly string _root;
    public LocalObjectStore(string root) => _root = Path.GetFullPath(root);
    public string Root => _root;
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
            if (IsInternalPath(file)) continue;
            var key = Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (prefix is null || key.StartsWith(prefix, StringComparison.Ordinal)) yield return new ObjectKey(key);
            await Task.Yield();
        }
    }
    public async ValueTask PutAsync(ObjectKey key, Stream content, long? length = null, CancellationToken cancellationToken = default)
    {
        var destination = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var keyLock = await AcquireKeyLockAsync(key, cancellationToken).ConfigureAwait(false);
        var temporary = await WriteTemporaryAsync(destination, content, cancellationToken).ConfigureAwait(false);
        try
        {
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async ValueTask DeleteAsync(ObjectKey key, CancellationToken cancellationToken = default)
    {
        await using var keyLock = await AcquireKeyLockAsync(key, cancellationToken).ConfigureAwait(false);
        var path = Resolve(key);
        if (File.Exists(path)) File.Delete(path);
    }
    public async ValueTask<bool> PutIfAbsentAsync(ObjectKey key, Stream content, long? length = null, CancellationToken cancellationToken = default)
    {
        var destination = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var keyLock = await AcquireKeyLockAsync(key, cancellationToken).ConfigureAwait(false);
        if (File.Exists(destination)) return false;
        var temporary = await WriteTemporaryAsync(destination, content, cancellationToken).ConfigureAwait(false);
        try
        {
            try { File.Move(temporary, destination, overwrite: false); return true; }
            catch (IOException) when (File.Exists(destination)) { return false; }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async ValueTask<bool> PutIfAbsentAsync(ObjectKey key, ContentHash expectedDigest, Stream content, long? length = null, CancellationToken cancellationToken = default)
    {
        ValidateCasKey(key, expectedDigest);
        var destination = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var keyLock = await AcquireKeyLockAsync(key, cancellationToken).ConfigureAwait(false);
        if (File.Exists(destination)) return false;
        var temporary = await WriteTemporaryAsync(destination, content, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var verification = File.OpenRead(temporary);
            if (await ContentHash.ComputeAsync(verification, HashAlgorithmId.Sha256, cancellationToken).ConfigureAwait(false) != expectedDigest)
                throw new CryptographicException($"Content does not match CAS digest '{expectedDigest}'.");
            File.Move(temporary, destination, overwrite: false);
            return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async ValueTask<bool> CompareAndSwapAsync(ObjectKey key, ObjectValidator expected, Stream content, long? length = null, CancellationToken cancellationToken = default)
    {
        var destination = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var keyLock = await AcquireKeyLockAsync(key, cancellationToken).ConfigureAwait(false);
        var info = new FileInfo(destination);
        if (!info.Exists || expected.Kind != ObjectValidatorKind.SizeAndMtime || Validator(info).Value != expected.Value) return false;
        var temporary = await WriteTemporaryAsync(destination, content, cancellationToken).ConfigureAwait(false);
        try { File.Move(temporary, destination, overwrite: true); return true; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async ValueTask CopyAsync(ObjectKey source, ObjectKey destination, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        await using var keyLock = await AcquireKeyLockAsync(destination, cancellationToken).ConfigureAwait(false);
        var sourcePath = Resolve(source); var destinationPath = Resolve(destination); Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath, overwrite);
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

    private static void ValidateCasKey(ObjectKey key, ContentHash expectedDigest)
    {
        if (expectedDigest.Algorithm != HashAlgorithmId.Sha256 || !string.Equals(key.Value.Split('/').Last(), Convert.ToHexString(expectedDigest.Value.Span).ToLowerInvariant(), StringComparison.Ordinal))
            throw new FormatException("Content-addressed writes require a sha256 digest encoded in the final object-key segment.");
    }

    private async ValueTask<string> WriteTemporaryAsync(string destination, Stream content, CancellationToken cancellationToken)
    {
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        return temporary;
    }

    private async ValueTask<FileStream> AcquireKeyLockAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        var lockRoot = Path.Combine(_root, ".4sup-locks");
        Directory.CreateDirectory(lockRoot);
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.Value))).ToLowerInvariant() + ".lock";
        var path = Path.Combine(lockRoot, name);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous); }
            catch (IOException) { await Task.Delay(25, cancellationToken).ConfigureAwait(false); }
        }
    }

    private bool IsInternalPath(string path)
    {
        var lockRoot = Path.Combine(_root, ".4sup-locks") + Path.DirectorySeparatorChar;
        return path.StartsWith(lockRoot, StringComparison.Ordinal) || Path.GetFileName(path).Contains(".tmp-", StringComparison.Ordinal);
    }
}
