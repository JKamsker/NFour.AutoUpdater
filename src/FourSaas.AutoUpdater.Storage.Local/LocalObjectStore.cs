using FourSaas.AutoUpdater.Core;
using FourSaas.AutoUpdater.Storage;

namespace FourSaas.AutoUpdater.Storage.Local;

public sealed class LocalObjectStore : IDelimitedObjectStore, IRangeReadableObjectStore, IConditionalWriteStore, IContentAddressedWriteStore, IServerSideCopyStore, IServerSideTransferStore, IServerSideVerifier
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
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (offset > info.Length)
            return ValueTask.FromResult<ReadResult?>(new ReadResult { Content = Stream.Null, ActualStartOffset = 0, StatusCode = 416, Validator = validator });
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
        // Enumeration must not follow links: a single junction planted inside the root would
        // otherwise report files from outside it as store objects, and callers that delete
        // what enumeration returns would delete them.
        foreach (var file in LinkSafeDirectoryWalk.EnumerateFiles(_root).OrderBy(static x => x, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsInternalPath(file)) continue;
            var key = Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (prefix is null || key.StartsWith(prefix, StringComparison.Ordinal)) yield return new ObjectKey(key);
            await Task.Yield();
        }
    }
    public async IAsyncEnumerable<ObjectListing> ListAsync(string? prefix, string delimiter, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(delimiter)) throw new ArgumentException("A delimiter is required.", nameof(delimiter));
        var root = prefix ?? string.Empty;
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var key in ListAsync(prefix, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var remainder = key.Value[root.Length..];
            var separator = remainder.IndexOf(delimiter, StringComparison.Ordinal);
            if (separator < 0) yield return new ObjectListing(key, null);
            else if (prefixes.Add(key.Value[..(root.Length + separator + delimiter.Length)])) yield return new ObjectListing(null, key.Value[..(root.Length + separator + delimiter.Length)]);
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
            FlushContainingDirectory(destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async ValueTask DeleteAsync(ObjectKey key, CancellationToken cancellationToken = default)
    {
        await using var keyLock = await AcquireKeyLockAsync(key, cancellationToken).ConfigureAwait(false);
        var path = Resolve(key);
        if (File.Exists(path))
        {
            File.Delete(path);
            FlushContainingDirectory(path);
        }
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
            try { File.Move(temporary, destination, overwrite: false); FlushContainingDirectory(destination); return true; }
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
            // The verification handle must be closed before the rename: File.OpenRead takes
            // FileShare.Read, which does not permit a concurrent rename, so leaving it open
            // across the Move fails with a sharing violation on Windows.
            await using (var verification = File.OpenRead(temporary))
            {
                if (await ContentHash.ComputeAsync(verification, HashAlgorithmId.Sha256, cancellationToken).ConfigureAwait(false) != expectedDigest)
                    throw new CryptographicException($"Content does not match CAS digest '{expectedDigest}'.");
            }
            File.Move(temporary, destination, overwrite: false);
            FlushContainingDirectory(destination);
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
        try { File.Move(temporary, destination, overwrite: true); FlushContainingDirectory(destination); return true; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async ValueTask CopyAsync(ObjectKey source, ObjectKey destination, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        await using var keyLock = await AcquireKeyLockAsync(destination, cancellationToken).ConfigureAwait(false);
        var sourcePath = Resolve(source); var destinationPath = Resolve(destination); Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (!overwrite && File.Exists(destinationPath)) throw new IOException($"Destination '{destination}' already exists.");
        var temporary = destinationPath + ".copy-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destinationPath, overwrite);
            FlushContainingDirectory(destinationPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async ValueTask<bool> TryCopyFromAsync(IReadableObjectStore sourceStore, ObjectKey source, ObjectKey destination, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        if (sourceStore is not LocalObjectStore local) return false;
        var sourcePath = local.Resolve(source);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException(source.Value);
        await using var keyLock = await AcquireKeyLockAsync(destination, cancellationToken).ConfigureAwait(false);
        var destinationPath = Resolve(destination);
        if (!overwrite && File.Exists(destinationPath)) throw new IOException($"Destination '{destination}' already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temporary = destinationPath + ".copy-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destinationPath, overwrite);
            FlushContainingDirectory(destinationPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return true;
    }
    public async ValueTask<bool> VerifyAsync(ObjectKey key, ContentHash expected, CancellationToken cancellationToken = default)
    {
        var path = Resolve(key); if (!File.Exists(path)) return false;
        await using var stream = File.OpenRead(path);
        return await ContentHash.ComputeAsync(stream, expected.Algorithm, cancellationToken).ConfigureAwait(false) == expected;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Maps an object key to an absolute path inside the store root.
    ///
    /// Lexical containment alone is not a containment boundary: <c>Path.GetFullPath</c>
    /// normalises "..", but it does not resolve links, so a symlink or Windows junction
    /// planted anywhere inside the root redirects the resulting read, write, copy or delete
    /// to an arbitrary location that still passes the prefix test.  Every component between
    /// the root and the leaf is therefore checked for a reparse point and rejected.
    ///
    /// Residual risk, stated plainly: these are path-based checks, so an attacker who can
    /// create links inside the root and win a race between the check and the subsequent open
    /// can still redirect an operation.  Closing that window entirely requires opening each
    /// component handle-relatively with no-follow and mutating through those handles.  This
    /// implementation raises the bar from "any planted link succeeds" to "only a won race
    /// succeeds"; deployments that cannot guarantee an exclusively-owned store root should
    /// not treat the local backend as a security boundary.
    /// </summary>
    private string Resolve(ObjectKey key)
    {
        var combined = Path.GetFullPath(Path.Combine(_root, key.Value.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !string.Equals(combined, _root, StringComparison.Ordinal)) throw new IOException("Object key escapes the store root.");
        EnsureNoLinksBelowRoot(combined);
        return combined;
    }

    /// Rejects a path if any existing component strictly below the store root is a symlink,
    /// junction or other reparse point.  Components that do not exist yet cannot redirect
    /// anything, so their absence is not an error.
    private void EnsureNoLinksBelowRoot(string absolutePath)
    {
        var rootLength = _root.TrimEnd(Path.DirectorySeparatorChar).Length;
        if (absolutePath.Length <= rootLength) return;
        for (var index = absolutePath.IndexOf(Path.DirectorySeparatorChar, rootLength + 1); ; index = absolutePath.IndexOf(Path.DirectorySeparatorChar, index + 1))
        {
            var component = index < 0 ? absolutePath : absolutePath[..index];
            if (LinkSafeDirectoryWalk.IsLink(component)) throw new IOException($"Refusing to traverse a link inside the object-store root: '{component}'.");
            if (index < 0) break;
        }
    }
    private static ObjectValidator Validator(FileInfo info) => new(ObjectValidatorKind.SizeAndMtime, $"{info.Length}:{info.LastWriteTimeUtc.Ticks}", false);

    private static void ValidateCasKey(ObjectKey key, ContentHash expectedDigest)
    {
        if (expectedDigest.Algorithm != HashAlgorithmId.Sha256 || !string.Equals(key.Value.Split('/').Last(), Convert.ToHexString(expectedDigest.Span).ToLowerInvariant(), StringComparison.Ordinal))
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

    private const int LockAcquireTimeoutMilliseconds = 30_000;
    private const int LockRetryInitialDelayMilliseconds = 5;
    private const int LockRetryMaximumDelayMilliseconds = 250;

    /// <summary>
    /// How long an ERROR_ACCESS_DENIED on the lock file is treated as Windows delete-pending
    /// rather than a real permission failure. Delete-pending clears in microseconds; a genuine
    /// permission error raises the same exception and must surface quickly.
    /// </summary>
    private const int DeletePendingRetryWindowMilliseconds = 2_000;

    private async ValueTask<FileStream> AcquireKeyLockAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        var lockRoot = Path.Combine(_root, ".4sup-locks");
        Directory.CreateDirectory(lockRoot);
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.Value))).ToLowerInvariant() + ".lock";
        var path = Path.Combine(lockRoot, name);

        // On Windows the lock file is removed when the handle closes, so per-key lock files
        // do not accumulate for the lifetime of the store.  On Unix, DeleteOnClose unlinks
        // the path while other waiters may still hold advisory locks on that inode, which
        // would let a waiter and a fresh creator both believe they own the key; there the
        // zero-byte file is deliberately left in place.
        var options = FileOptions.Asynchronous | (OperatingSystem.IsWindows() ? FileOptions.DeleteOnClose : FileOptions.None);

        var start = Environment.TickCount64;
        var deadline = start + LockAcquireTimeoutMilliseconds;
        var delay = LockRetryInitialDelayMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, options); }
            catch (IOException ex) when (IsContention(ex))
            {
                // Only genuine contention is retried.  Treating every IOException as a busy
                // lock turns a permission error, a full disk or an invalid path into an
                // indefinite spin that reports nothing useful.
                if (Environment.TickCount64 >= deadline)
                    throw new TimeoutException($"Timed out after {LockAcquireTimeoutMilliseconds} ms waiting for the object-store lock on '{key}'.", ex);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = Math.Min(delay * 2, LockRetryMaximumDelayMilliseconds);
            }
            catch (UnauthorizedAccessException ex) when (Environment.TickCount64 - start < DeletePendingRetryWindowMilliseconds)
            {
                // Windows delete-pending. Closing the previous holder's DeleteOnClose handle
                // unlinks the file, and an open that lands in that window fails with
                // ERROR_ACCESS_DENIED — surfaced here as UnauthorizedAccessException rather
                // than the sharing violation the contention check above expects.
                //
                // The state clears almost immediately, so it is retried only briefly. A real
                // permission problem raises the same exception type and must not be retried
                // for the full lock timeout, which is why this window is separate and short.
                _ = ex;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = Math.Min(delay * 2, LockRetryMaximumDelayMilliseconds);
            }
        }
    }

    private static bool IsContention(IOException exception)
    {
        const int ErrorSharingViolation = 32;
        const int ErrorLockViolation = 33;
        const int EwouldblockLinux = 11;
        const int EwouldblockDarwin = 35;
        if (exception is FileNotFoundException or DirectoryNotFoundException or PathTooLongException) return false;
        var code = exception.HResult & 0xFFFF;
        return code is ErrorSharingViolation or ErrorLockViolation or EwouldblockLinux
            || (OperatingSystem.IsMacOS() && code == EwouldblockDarwin);
    }

    private bool IsInternalPath(string path)
    {
        var lockRoot = Path.Combine(_root, ".4sup-locks") + Path.DirectorySeparatorChar;
        var fileName = Path.GetFileName(path);
        return path.StartsWith(lockRoot, StringComparison.Ordinal)
            || fileName.Contains(".tmp-", StringComparison.Ordinal)
            || fileName.Contains(".copy-", StringComparison.Ordinal);
    }

    private static void FlushContainingDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            using var directory = new FileStream(Path.GetDirectoryName(path)!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            directory.Flush(flushToDisk: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
