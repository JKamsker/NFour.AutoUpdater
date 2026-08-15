using NFour.AutoUpdater.Core;

namespace NFour.AutoUpdater.Storage;

/// <summary>Identifies operations supported by an object-store transport.</summary>
[Flags]
public enum StorageCapabilities
{
    /// <summary>No storage operations are supported.</summary>
    None = 0,
    /// <summary>Objects can be read.</summary>
    Read = 1,
    /// <summary>Validated byte ranges can be read.</summary>
    Range = 2,
    /// <summary>Object keys can be listed.</summary>
    List = 4,
    /// <summary>Objects can be written.</summary>
    Write = 8,
    /// <summary>Writes can be conditioned on object state.</summary>
    ConditionalWrite = 16,
    /// <summary>Objects can be copied within storage without relaying content.</summary>
    ServerSideCopy = 32,
    /// <summary>Direct upload URLs can be created.</summary>
    Presigning = 64,
    /// <summary>Multipart uploads are supported.</summary>
    Multipart = 128,
    /// <summary>Objects can be deleted.</summary>
    Delete = 256
}

/// <summary>Negotiates declared repository capabilities with a concrete transport.</summary>
public static class StorageCapabilityNegotiation
{
    /// <summary>Returns capabilities supported by both a descriptor and transport.</summary>
    public static StorageCapabilities Intersect(RepositoryDescriptor descriptor, IReadableObjectStore transport)
        => descriptor.Capabilities & transport.Capabilities;

    /// <summary>Determines whether capability flags agree with implemented storage ports.</summary>
    public static bool IsConsistent(IReadableObjectStore transport)
    {
        var capabilities = transport.Capabilities;
        if (!capabilities.HasFlag(StorageCapabilities.Read)) return false;
        if (transport is IRangeReadableObjectStore != capabilities.HasFlag(StorageCapabilities.Range)) return false;
        if (transport is IListableObjectStore != capabilities.HasFlag(StorageCapabilities.List)) return false;
        if (transport is IWritableObjectStore != capabilities.HasFlag(StorageCapabilities.Write)) return false;
        if (transport is IWritableObjectStore != capabilities.HasFlag(StorageCapabilities.Delete)) return false;
        if (transport is IConditionalWriteStore != capabilities.HasFlag(StorageCapabilities.ConditionalWrite)) return false;
        if (transport is IServerSideCopyStore != capabilities.HasFlag(StorageCapabilities.ServerSideCopy)) return false;
        if (transport is IPresigningStore != capabilities.HasFlag(StorageCapabilities.Presigning)) return false;
        if (transport is IMultipartUploadStore != capabilities.HasFlag(StorageCapabilities.Multipart)) return false;
        return true;
    }
}

/// <summary>Represents a validated relative object-store key.</summary>
public readonly record struct ObjectKey
{
    /// <summary>Initializes a validated object key.</summary>
    /// <param name="value">The relative slash-separated key.</param>
    public ObjectKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('/') || value.Contains('\\') || value.Contains("..", StringComparison.Ordinal) || value.Contains('%') || value.Contains('?') || value.Contains('#') || value.Contains(':') || value.Any(char.IsControl) || value.Split('/').Any(static segment => segment.Length == 0 || segment is "." or ".."))
            throw new ArgumentException("Object keys must be relative URL paths and cannot contain traversal or URI syntax.", nameof(value));
        Value = value;
    }
    /// <summary>Gets the canonical object key.</summary>
    public string Value { get; }
    /// <summary>Returns the canonical object key.</summary>
    public override string ToString() => Value;
}

/// <summary>Identifies the value used to validate object identity.</summary>
public enum ObjectValidatorKind
{
    /// <summary>No validator is available.</summary>
    None,
    /// <summary>An HTTP-compatible entity tag.</summary>
    ETag,
    /// <summary>A last-modified timestamp.</summary>
    LastModified,
    /// <summary>A composite file size and modification time.</summary>
    SizeAndMtime
}
/// <summary>Describes an object identity validator.</summary>
/// <param name="Kind">The validator representation.</param>
/// <param name="Value">The validator value.</param>
/// <param name="IsStrong">Whether the validator uniquely identifies exact bytes.</param>
public sealed record ObjectValidator(ObjectValidatorKind Kind, string Value, bool IsStrong = true);
/// <summary>Describes object metadata without opening its content stream.</summary>
/// <param name="Length">The object length in bytes.</param>
/// <param name="Validator">The object identity validator.</param>
/// <param name="ContentEncoding">The optional content encoding.</param>
/// <param name="ContentType">The optional media type.</param>
/// <param name="AcceptRanges">Whether range reads are supported.</param>
/// <param name="LastModified">The optional last-modified time.</param>
/// <param name="CacheControl">The optional cache-control value.</param>
public sealed record ObjectHead(long Length, ObjectValidator? Validator, string? ContentEncoding = null, string? ContentType = null, bool AcceptRanges = true, DateTimeOffset? LastModified = null, string? CacheControl = null);

/// <summary>Owns an opened object stream and metadata describing the response.</summary>
public sealed class ReadResult : IAsyncDisposable
{
    /// <summary>Gets the readable object content.</summary>
    public required Stream Content { get; init; }
    /// <summary>Gets the actual starting byte offset returned by storage.</summary>
    public required long ActualStartOffset { get; init; }
    /// <summary>Gets the validator for the opened object.</summary>
    public required ObjectValidator? Validator { get; init; }
    /// <summary>Gets the transport status code.</summary>
    public int StatusCode { get; init; } = 200;
    /// <summary>Gets the optional content encoding.</summary>
    public string? ContentEncoding { get; init; }
    /// <inheritdoc />
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>Provides read access to immutable repository objects.</summary>
public interface IReadableObjectStore : IAsyncDisposable
{
    /// <summary>Gets the operations supported by this store.</summary>
    StorageCapabilities Capabilities { get; }
    /// <summary>Gets the recommended maximum concurrent operations.</summary>
    int RecommendedParallelism { get; }
    /// <summary>Opens an object, optionally from a validated byte offset.</summary>
    ValueTask<ReadResult?> OpenAsync(ObjectKey key, long offset = 0, ObjectValidator? ifMatch = null, CancellationToken cancellationToken = default);
    /// <summary>Gets object metadata without opening content.</summary>
    ValueTask<ObjectHead?> HeadAsync(ObjectKey key, CancellationToken cancellationToken = default);
}

/// <summary>Marks a transport whose open operation honors validated byte ranges.</summary>
public interface IRangeReadableObjectStore : IReadableObjectStore { }

/// <summary>Provides flat object-key enumeration.</summary>
public interface IListableObjectStore : IReadableObjectStore
{
    /// <summary>Enumerates object keys below an optional prefix.</summary>
    IAsyncEnumerable<ObjectKey> ListAsync(string? prefix = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional hierarchical listing.  Object stores that cannot represent common
/// prefixes continue to implement <see cref="IListableObjectStore"/> only.
/// </summary>
public interface IDelimitedObjectStore : IListableObjectStore
{
    /// <summary>Enumerates objects and common prefixes using a hierarchy delimiter.</summary>
    IAsyncEnumerable<ObjectListing> ListAsync(string? prefix, string delimiter, CancellationToken cancellationToken = default);
}

/// <summary>Represents either an object or a common prefix from a delimited listing.</summary>
/// <param name="Object">The object key, when the item is an object.</param>
/// <param name="CommonPrefix">The common prefix, when the item represents a subtree.</param>
public sealed record ObjectListing(ObjectKey? Object, string? CommonPrefix)
{
    /// <summary>Gets whether the item represents a common prefix.</summary>
    public bool IsPrefix => CommonPrefix is not null;
}

/// <summary>Provides ordinary object writes and deletion.</summary>
public interface IWritableObjectStore : IReadableObjectStore
{
    /// <summary>Writes or replaces an object.</summary>
    ValueTask PutAsync(ObjectKey key, Stream content, long? length = null, CancellationToken cancellationToken = default);
    /// <summary>Deletes an object when it exists.</summary>
    ValueTask DeleteAsync(ObjectKey key, CancellationToken cancellationToken = default);
}

/// <summary>Provides atomic writes conditioned on current object state.</summary>
public interface IConditionalWriteStore : IWritableObjectStore
{
    /// <summary>Writes an object only when the key does not exist.</summary>
    ValueTask<bool> PutIfAbsentAsync(ObjectKey key, Stream content, long? length = null, CancellationToken cancellationToken = default);
    /// <summary>Replaces an object only when its validator matches.</summary>
    ValueTask<bool> CompareAndSwapAsync(ObjectKey key, ObjectValidator expected, Stream content, long? length = null, CancellationToken cancellationToken = default);
}

/// <summary>Conditional writes that also bind the SHA-256 CAS key to the uploaded bytes.</summary>
public interface IContentAddressedWriteStore
{
    /// <summary>Writes an absent object while requiring content to match its expected digest.</summary>
    ValueTask<bool> PutIfAbsentAsync(ObjectKey key, ContentHash expectedDigest, Stream content, long? length = null, CancellationToken cancellationToken = default);
}

/// <summary>Copies objects within one storage service.</summary>
public interface IServerSideCopyStore : IWritableObjectStore
{
    /// <summary>Copies one object to another key without relaying bytes through the caller.</summary>
    ValueTask CopyAsync(ObjectKey source, ObjectKey destination, bool overwrite = false, CancellationToken cancellationToken = default);
}

/// <summary>Optional server-side transfer between separate staging and served store instances.</summary>
public interface IServerSideTransferStore
{
    /// <summary>Attempts a storage-side copy from another store instance.</summary>
    ValueTask<bool> TryCopyFromAsync(IReadableObjectStore sourceStore, ObjectKey source, ObjectKey destination, bool overwrite = false, CancellationToken cancellationToken = default);
}

/// <summary>Identifies an in-progress multipart upload.</summary>
/// <param name="Key">The destination object key.</param>
/// <param name="UploadId">The storage-assigned upload identifier.</param>
/// <param name="PartSize">The required non-final part size.</param>
public sealed record MultipartUpload(ObjectKey Key, string UploadId, long PartSize);
/// <summary>Describes one completed multipart upload part.</summary>
/// <param name="Number">The one-based part number.</param>
/// <param name="ETag">The storage-assigned entity tag.</param>
/// <param name="Length">The part length in bytes.</param>
/// <param name="Checksum">The optional part checksum.</param>
public sealed record MultipartPart(int Number, string ETag, long Length, ContentHash? Checksum = null);

/// <summary>A single direct-to-storage URL for one part of a brokered multipart grant.</summary>
/// <param name="Number">The one-based part number.</param>
/// <param name="Uri">The presigned upload URI.</param>
/// <param name="RequiredHeaders">Headers the uploader must include.</param>
public sealed record PresignedUploadPart(int Number, Uri Uri, ImmutableDictionary<string, string> RequiredHeaders);

/// <summary>The storage-side state needed to expose a multipart upload without relaying bytes through the API.</summary>
/// <param name="Upload">The underlying multipart upload.</param>
/// <param name="Parts">The presigned part descriptors.</param>
public sealed record BrokeredMultipartUpload(MultipartUpload Upload, ImmutableArray<PresignedUploadPart> Parts);

/// <summary>
/// A capability-gated multipart port. Backends that do not implement multipart do not expose
/// this interface, even if they support ordinary writes.
/// </summary>
public interface IMultipartUploadStore
{
    /// <summary>Starts a multipart upload for an object key.</summary>
    ValueTask<MultipartUpload> StartMultipartUploadAsync(ObjectKey key, CancellationToken cancellationToken = default);
    /// <summary>Finds an incomplete multipart upload for an object key.</summary>
    ValueTask<MultipartUpload?> FindIncompleteMultipartUploadAsync(ObjectKey key, CancellationToken cancellationToken = default);
    /// <summary>Lists already uploaded parts.</summary>
    ValueTask<IReadOnlyList<MultipartPart>> ListPartsAsync(MultipartUpload upload, CancellationToken cancellationToken = default);
    /// <summary>Uploads one part from a content stream.</summary>
    ValueTask<MultipartPart> UploadPartAsync(MultipartUpload upload, int partNumber, Stream content, long length, CancellationToken cancellationToken = default);
    /// <summary>Copies one source byte range into a multipart part.</summary>
    ValueTask<MultipartPart> CopyPartAsync(MultipartUpload upload, ObjectKey source, int partNumber, long firstByte, long lastByte, CancellationToken cancellationToken = default);
    /// <summary>Completes a multipart upload from its ordered parts.</summary>
    ValueTask CompleteMultipartUploadAsync(MultipartUpload upload, IReadOnlyList<MultipartPart> parts, CancellationToken cancellationToken = default);
    /// <summary>Aborts an incomplete multipart upload.</summary>
    ValueTask AbortMultipartUploadAsync(MultipartUpload upload, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional broker port for S3-style multipart grants.  The management API receives only
/// the upload metadata and part ETags; payload bytes continue to go directly to storage.
/// </summary>
public interface IMultipartGrantStore
{
    /// <summary>Creates a multipart upload and presigned part URLs for a grant.</summary>
    ValueTask<BrokeredMultipartUpload> CreateBrokeredMultipartUploadAsync(UploadGrantDescriptor descriptor, CancellationToken cancellationToken = default);
    /// <summary>Lists parts uploaded through a brokered grant.</summary>
    ValueTask<IReadOnlyList<MultipartPart>> ListBrokeredMultipartPartsAsync(MultipartUpload upload, CancellationToken cancellationToken = default);
    /// <summary>Completes a brokered multipart upload.</summary>
    ValueTask CompleteBrokeredMultipartUploadAsync(MultipartUpload upload, IReadOnlyList<MultipartPart> parts, CancellationToken cancellationToken = default);
    /// <summary>Aborts a brokered multipart upload.</summary>
    ValueTask AbortBrokeredMultipartUploadAsync(MultipartUpload upload, CancellationToken cancellationToken = default);
}

/// <summary>Aborts abandoned brokered multipart uploads during staging GC.</summary>
public interface IMultipartGarbageCollector
{
    /// <summary>Aborts abandoned multipart uploads below a staging prefix.</summary>
    ValueTask<int> AbortIncompleteMultipartUploadsAsync(string prefix, IReadOnlySet<string> preservedPrefixes, bool dryRun = false, CancellationToken cancellationToken = default);
}

/// <summary>Defines the immutable constraints for a direct upload grant.</summary>
/// <param name="StagingKey">The grant-specific staging object key.</param>
/// <param name="ExpectedDigest">The required content digest.</param>
/// <param name="ExpectedLength">The required content length.</param>
/// <param name="ExpiresAt">The grant expiration time.</param>
public sealed record UploadGrantDescriptor(ObjectKey StagingKey, ContentHash ExpectedDigest, long ExpectedLength, DateTimeOffset ExpiresAt);
/// <summary>Creates time-bounded direct upload URIs.</summary>
public interface IPresigningStore
{
    /// <summary>Creates a direct upload URI constrained by a grant descriptor.</summary>
    ValueTask<Uri> CreateUploadUriAsync(UploadGrantDescriptor descriptor, CancellationToken cancellationToken = default);
}
/// <summary>Provides headers required when using a direct upload URI.</summary>
public interface IUploadHeaderProvider
{
    /// <summary>Gets storage-required upload headers for a grant.</summary>
    IReadOnlyDictionary<string, string> GetRequiredUploadHeaders(UploadGrantDescriptor descriptor);
}
