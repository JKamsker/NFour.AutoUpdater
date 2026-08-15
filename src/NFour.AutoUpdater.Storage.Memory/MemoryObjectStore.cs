using NFour.AutoUpdater.Core;
using NFour.AutoUpdater.Storage;

namespace NFour.AutoUpdater.Storage.Memory;

/// <summary>Provides a concurrent, process-local object store for tests and ephemeral workflows.</summary>
public sealed class MemoryObjectStore : IDelimitedObjectStore, IRangeReadableObjectStore, IConditionalWriteStore, IContentAddressedWriteStore, IServerSideCopyStore, IServerSideVerifier
{
    private sealed record Entry(byte[] Bytes, DateTimeOffset LastModified, string ETag);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private static readonly StringComparer Comparer = StringComparer.Ordinal;

    /// <inheritdoc />
    public StorageCapabilities Capabilities => StorageCapabilities.Read | StorageCapabilities.Range | StorageCapabilities.List | StorageCapabilities.Write | StorageCapabilities.ConditionalWrite | StorageCapabilities.ServerSideCopy | StorageCapabilities.Delete;
    /// <inheritdoc />
    public int RecommendedParallelism => 32;

    /// <inheritdoc />
    public ValueTask<ReadResult?> OpenAsync(ObjectKey key, long offset = 0, ObjectValidator? ifMatch = null, CancellationToken cancellationToken = default)
    {
        if (!_entries.TryGetValue(key.Value, out var entry)) return ValueTask.FromResult<ReadResult?>(null);
        if (ifMatch is not null && !string.Equals(ifMatch.Value, entry.ETag, StringComparison.Ordinal))
            return ValueTask.FromResult<ReadResult?>(new ReadResult { Content = new MemoryStream(entry.Bytes, writable: false), ActualStartOffset = 0, StatusCode = 200, Validator = new(ObjectValidatorKind.ETag, entry.ETag) });
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (offset > entry.Bytes.LongLength)
            return ValueTask.FromResult<ReadResult?>(new ReadResult { Content = Stream.Null, ActualStartOffset = 0, StatusCode = 416, Validator = new(ObjectValidatorKind.ETag, entry.ETag) });
        // publiclyVisible: false — with it set, a caller can retrieve the underlying array via
        // MemoryStream.GetBuffer and mutate the stored object in place while the recorded
        // ETag and validator continue to describe the original bytes.
        return ValueTask.FromResult<ReadResult?>(new ReadResult { Content = new MemoryStream(entry.Bytes, (int)offset, entry.Bytes.Length - (int)offset, writable: false, publiclyVisible: false), ActualStartOffset = offset, StatusCode = offset == 0 ? 200 : 206, Validator = new(ObjectValidatorKind.ETag, entry.ETag) });
    }
    /// <inheritdoc />
    public ValueTask<ObjectHead?> HeadAsync(ObjectKey key, CancellationToken cancellationToken = default) => ValueTask.FromResult(_entries.TryGetValue(key.Value, out var entry) ? new ObjectHead(entry.Bytes.LongLength, new(ObjectValidatorKind.ETag, entry.ETag), LastModified: entry.LastModified) : null);
    /// <inheritdoc />
    public async IAsyncEnumerable<ObjectKey> ListAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var key in _entries.Keys.OrderBy(static x => x, Comparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (prefix is null || key.StartsWith(prefix, StringComparison.Ordinal)) yield return new ObjectKey(key);
            await Task.Yield();
        }
    }
    /// <inheritdoc />
    public async IAsyncEnumerable<ObjectListing> ListAsync(string? prefix, string delimiter, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(delimiter)) throw new ArgumentException("A delimiter is required.", nameof(delimiter));
        var root = prefix ?? string.Empty;
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in _entries.Keys.OrderBy(static x => x, Comparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!key.StartsWith(root, StringComparison.Ordinal)) continue;
            var remainder = key[root.Length..];
            var separator = remainder.IndexOf(delimiter, StringComparison.Ordinal);
            if (separator < 0) yield return new ObjectListing(new ObjectKey(key), null);
            else if (prefixes.Add(key[..(root.Length + separator + delimiter.Length)])) yield return new ObjectListing(null, key[..(root.Length + separator + delimiter.Length)]);
            await Task.Yield();
        }
    }
    /// <inheritdoc />
    public async ValueTask PutAsync(ObjectKey key, Stream content, long? length = null, CancellationToken cancellationToken = default) => _entries[key.Value] = await CreateEntryAsync(content, cancellationToken).ConfigureAwait(false);
    /// <inheritdoc />
    public ValueTask DeleteAsync(ObjectKey key, CancellationToken cancellationToken = default) { _entries.TryRemove(key.Value, out _); return ValueTask.CompletedTask; }
    /// <inheritdoc />
    public async ValueTask<bool> PutIfAbsentAsync(ObjectKey key, Stream content, long? length = null, CancellationToken cancellationToken = default)
    {
        var entry = await CreateEntryAsync(content, cancellationToken).ConfigureAwait(false);
        return _entries.TryAdd(key.Value, entry);
    }
    /// <inheritdoc />
    public async ValueTask<bool> CompareAndSwapAsync(ObjectKey key, ObjectValidator expected, Stream content, long? length = null, CancellationToken cancellationToken = default)
    {
        if (expected.Kind != ObjectValidatorKind.ETag || !expected.IsStrong) return false;
        var entry = await CreateEntryAsync(content, cancellationToken).ConfigureAwait(false);
        while (_entries.TryGetValue(key.Value, out var existing))
        {
            if (existing.ETag != expected.Value) return false;
            if (_entries.TryUpdate(key.Value, entry, existing)) return true;
        }
        return false;
    }
    /// <inheritdoc />
    public async ValueTask<bool> PutIfAbsentAsync(ObjectKey key, ContentHash expectedDigest, Stream content, long? length = null, CancellationToken cancellationToken = default)
    {
        if (expectedDigest.Algorithm != HashAlgorithmId.Sha256 || !string.Equals(key.Value.Split('/').Last(), Convert.ToHexString(expectedDigest.Span).ToLowerInvariant(), StringComparison.Ordinal))
            throw new FormatException("Content-addressed writes require a sha256 digest encoded in the final object-key segment.");
        var entry = await CreateEntryAsync(content, cancellationToken).ConfigureAwait(false);
        if (ContentHash.Compute(entry.Bytes, HashAlgorithmId.Sha256) != expectedDigest) throw new CryptographicException($"Content does not match CAS digest '{expectedDigest}'.");
        return _entries.TryAdd(key.Value, entry);
    }
    /// <inheritdoc />
    public ValueTask CopyAsync(ObjectKey source, ObjectKey destination, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        if (!_entries.TryGetValue(source.Value, out var entry)) throw new FileNotFoundException(source.Value);
        var copy = entry with { Bytes = entry.Bytes.ToArray(), LastModified = DateTimeOffset.UtcNow };
        if (!overwrite && !_entries.TryAdd(destination.Value, copy)) throw new IOException("Destination already exists.");
        if (overwrite) _entries[destination.Value] = copy;
        return ValueTask.CompletedTask;
    }
    /// <inheritdoc />
    public async ValueTask<bool> VerifyAsync(ObjectKey key, ContentHash expected, CancellationToken cancellationToken = default)
    {
        await using var result = await OpenAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result is null) return false;
        return ContentHash.Compute(await ReadAllAsync(result.Content, cancellationToken).ConfigureAwait(false), expected.Algorithm) == expected;
    }
    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async ValueTask<Entry> CreateEntryAsync(Stream content, CancellationToken cancellationToken)
    {
        await using var copy = new MemoryStream();
        await content.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        var bytes = copy.ToArray();
        return new Entry(bytes, DateTimeOffset.UtcNow, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }
    private static async ValueTask<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken) { using var memory = new MemoryStream(); await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false); return memory.ToArray(); }
}
