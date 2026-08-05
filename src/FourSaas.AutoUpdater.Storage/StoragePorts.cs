using FourSaas.AutoUpdater.Core;

namespace FourSaas.AutoUpdater.Storage;

[Flags]
public enum StorageCapabilities
{
    None = 0,
    Read = 1,
    Range = 2,
    List = 4,
    Write = 8,
    ConditionalWrite = 16,
    ServerSideCopy = 32,
    Presigning = 64,
    Multipart = 128,
    Delete = 256
}

public static class StorageCapabilityNegotiation
{
    public static StorageCapabilities Intersect(RepositoryDescriptor descriptor, IReadableObjectStore transport)
        => descriptor.Capabilities & transport.Capabilities;

    public static bool IsConsistent(IReadableObjectStore transport)
    {
        var capabilities = transport.Capabilities;
        if (transport is IListableObjectStore != capabilities.HasFlag(StorageCapabilities.List)) return false;
        if (transport is IWritableObjectStore != capabilities.HasFlag(StorageCapabilities.Write)) return false;
        if (transport is IConditionalWriteStore != capabilities.HasFlag(StorageCapabilities.ConditionalWrite)) return false;
        if (transport is IServerSideCopyStore != capabilities.HasFlag(StorageCapabilities.ServerSideCopy)) return false;
        if (transport is IPresigningStore != capabilities.HasFlag(StorageCapabilities.Presigning)) return false;
        return true;
    }
}

public readonly record struct ObjectKey
{
    public ObjectKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('/') || value.Contains("..", StringComparison.Ordinal) || value.Any(char.IsControl)) throw new ArgumentException("Object keys must be relative and cannot contain traversal.", nameof(value));
        Value = value;
    }
    public string Value { get; }
    public override string ToString() => Value;
}

public enum ObjectValidatorKind { None, ETag, LastModified, SizeAndMtime }
public sealed record ObjectValidator(ObjectValidatorKind Kind, string Value, bool IsStrong = true);
public sealed record ObjectHead(long Length, ObjectValidator? Validator, string? ContentEncoding = null, string? ContentType = null, bool AcceptRanges = true, DateTimeOffset? LastModified = null);

public sealed class ReadResult : IAsyncDisposable
{
    public required Stream Content { get; init; }
    public required long ActualStartOffset { get; init; }
    public required ObjectValidator? Validator { get; init; }
    public int StatusCode { get; init; } = 200;
    public string? ContentEncoding { get; init; }
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public interface IReadableObjectStore : IAsyncDisposable
{
    StorageCapabilities Capabilities { get; }
    int RecommendedParallelism { get; }
    ValueTask<ReadResult?> OpenAsync(ObjectKey key, long offset = 0, ObjectValidator? ifMatch = null, CancellationToken cancellationToken = default);
    ValueTask<ObjectHead?> HeadAsync(ObjectKey key, CancellationToken cancellationToken = default);
}

public interface IListableObjectStore : IReadableObjectStore
{
    IAsyncEnumerable<ObjectKey> ListAsync(string? prefix = null, CancellationToken cancellationToken = default);
}

public interface IWritableObjectStore : IReadableObjectStore
{
    ValueTask PutAsync(ObjectKey key, Stream content, long? length = null, CancellationToken cancellationToken = default);
    ValueTask DeleteAsync(ObjectKey key, CancellationToken cancellationToken = default);
}

public interface IConditionalWriteStore : IWritableObjectStore
{
    ValueTask<bool> PutIfAbsentAsync(ObjectKey key, Stream content, long? length = null, CancellationToken cancellationToken = default);
    ValueTask<bool> CompareAndSwapAsync(ObjectKey key, ObjectValidator expected, Stream content, long? length = null, CancellationToken cancellationToken = default);
}

public interface IServerSideCopyStore : IWritableObjectStore
{
    ValueTask CopyAsync(ObjectKey source, ObjectKey destination, bool overwrite = false, CancellationToken cancellationToken = default);
}

public sealed record UploadGrantDescriptor(ObjectKey StagingKey, ContentHash ExpectedDigest, long ExpectedLength, DateTimeOffset ExpiresAt);
public interface IPresigningStore
{
    ValueTask<Uri> CreateUploadUriAsync(UploadGrantDescriptor descriptor, CancellationToken cancellationToken = default);
}

public interface IServerSideVerifier
{
    ValueTask<bool> VerifyAsync(ObjectKey key, ContentHash expected, CancellationToken cancellationToken = default);
}

public sealed record RepositoryLayoutTemplates
{
    public string BlobTemplate { get; init; } = "blobs/{alg}/{h0:2}/{h2:2}/{hash}";
    public string PackageTemplate { get; init; } = "packages/{packageId}/{version}/package.json";
    public string PackageIndexTemplate { get; init; } = "packages/{packageId}/index{page}.json";
    public string ChannelTemplate { get; init; } = "products/{productId}/channels/{channel}.json";
    public string ReleaseTemplate { get; init; } = "products/{productId}/releases/{releaseId}/release.lock.json";
    public string ReleaseBundleTemplate { get; init; } = "products/{productId}/releases/{releaseId}/release.bundle.json";
    public string CoverageTemplate { get; init; } = "products/{productId}/releases/{releaseId}/coverage.json";
    public string ReleaseIndexTemplate { get; init; } = "products/{productId}/releases/index{page}.json";
}

public sealed record RepositoryDescriptor
{
    public int SchemaVersion { get; init; } = 1;
    public required string RepositoryId { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public RepositoryLayoutTemplates Layout { get; init; } = new();
    public string ContentHashAlgorithm { get; init; } = "sha256";
    public StorageCapabilities Capabilities { get; init; } = StorageCapabilities.Read | StorageCapabilities.Range;
    public string IntegrityGuarantee { get; init; } = "verified";
    public ImmutableArray<string> Products { get; init; } = [];
    public ImmutableArray<Uri> BlobBaseUrls { get; init; } = [];
    public string? MinimumClientVersion { get; init; }
    public ImmutableArray<TrustedKey> TrustedKeys { get; init; } = [];
}

public sealed record TrustedKey
{
    public required string KeyId { get; init; }
    public required string Algorithm { get; init; }
    public required string PublicKey { get; init; }
    public DateTimeOffset? NotBefore { get; init; }
    public DateTimeOffset? NotAfter { get; init; }
}

public sealed class RepositoryLayout(RepositoryLayoutTemplates templates)
{
    public ObjectKey Blob(ContentHash hash) => new(Format(templates.BlobTemplate, ("alg", Algorithm(hash.Algorithm)), ("h0", Hex(hash)[..2]), ("h2", Hex(hash)[2..4]), ("hash", Hex(hash))));
    public ObjectKey Package(PackageId id, PackageVersion version) => new(Format(templates.PackageTemplate, ("packageId", id.Value), ("version", version.Label)));
    public ObjectKey PackageIndex(PackageId id, int page = 0) => new(Format(templates.PackageIndexTemplate, ("packageId", id.Value), ("page", page.ToString(CultureInfo.InvariantCulture))));
    public ObjectKey Channel(string productId, string channel) => new(Format(templates.ChannelTemplate, ("productId", productId), ("channel", channel)));
    public ObjectKey Release(string productId, string releaseId) => new(Format(templates.ReleaseTemplate, ("productId", productId), ("releaseId", releaseId)));
    public ObjectKey ReleaseBundle(string productId, string releaseId) => new(Format(templates.ReleaseBundleTemplate, ("productId", productId), ("releaseId", releaseId)));
    public ObjectKey Coverage(string productId, string releaseId) => new(Format(templates.CoverageTemplate, ("productId", productId), ("releaseId", releaseId)));
    public ObjectKey ReleaseIndex(string productId, int page = 0) => new(Format(templates.ReleaseIndexTemplate, ("productId", productId), ("page", page.ToString(CultureInfo.InvariantCulture))));

    private static string Format(string template, params (string Name, string Value)[] values)
    {
        var result = template;
        foreach (var (name, value) in values) result = result.Replace("{" + name + "}", value, StringComparison.Ordinal).Replace("{" + name + ":2}", value, StringComparison.Ordinal).Replace("{" + name + ":4}", value, StringComparison.Ordinal);
        if (result.Contains('{') || result.Contains('}') || result.StartsWith('/') || result.Contains("..", StringComparison.Ordinal)) throw new FormatException("Repository template contains an unsupported placeholder or traversal.");
        return result.ToLowerInvariant();
    }
    private static string Hex(ContentHash hash) => Convert.ToHexString(hash.Value.Span).ToLowerInvariant();
    private static string Algorithm(HashAlgorithmId algorithm) => algorithm switch { HashAlgorithmId.Sha256 => "sha256", HashAlgorithmId.Sha512 => "sha512", HashAlgorithmId.Md5 => "md5", HashAlgorithmId.Blake3 => "blake3", _ => throw new ArgumentOutOfRangeException(nameof(algorithm)) };
}
