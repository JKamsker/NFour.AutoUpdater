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
        if (transport is IMultipartUploadStore != capabilities.HasFlag(StorageCapabilities.Multipart)) return false;
        return true;
    }
}

public readonly record struct ObjectKey
{
    public ObjectKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('/') || value.Contains('\\') || value.Contains("..", StringComparison.Ordinal) || value.Contains('%') || value.Contains('?') || value.Contains('#') || value.Contains(':') || value.Any(char.IsControl) || value.Split('/').Any(static segment => segment.Length == 0 || segment is "." or ".."))
            throw new ArgumentException("Object keys must be relative URL paths and cannot contain traversal or URI syntax.", nameof(value));
        Value = value;
    }
    public string Value { get; }
    public override string ToString() => Value;
}

public enum ObjectValidatorKind { None, ETag, LastModified, SizeAndMtime }
public sealed record ObjectValidator(ObjectValidatorKind Kind, string Value, bool IsStrong = true);
public sealed record ObjectHead(long Length, ObjectValidator? Validator, string? ContentEncoding = null, string? ContentType = null, bool AcceptRanges = true, DateTimeOffset? LastModified = null, string? CacheControl = null);

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

/// <summary>Conditional writes that also bind the SHA-256 CAS key to the uploaded bytes.</summary>
public interface IContentAddressedWriteStore
{
    ValueTask<bool> PutIfAbsentAsync(ObjectKey key, ContentHash expectedDigest, Stream content, long? length = null, CancellationToken cancellationToken = default);
}

public interface IServerSideCopyStore : IWritableObjectStore
{
    ValueTask CopyAsync(ObjectKey source, ObjectKey destination, bool overwrite = false, CancellationToken cancellationToken = default);
}

public sealed record MultipartUpload(ObjectKey Key, string UploadId, long PartSize);
public sealed record MultipartPart(int Number, string ETag, long Length, ContentHash? Checksum = null);

/// <summary>
/// A capability-gated multipart port. Backends that do not implement multipart do not expose
/// this interface, even if they support ordinary writes.
/// </summary>
public interface IMultipartUploadStore
{
    ValueTask<MultipartUpload> StartMultipartUploadAsync(ObjectKey key, CancellationToken cancellationToken = default);
    ValueTask<MultipartPart> UploadPartAsync(MultipartUpload upload, int partNumber, Stream content, long length, CancellationToken cancellationToken = default);
    ValueTask<MultipartPart> CopyPartAsync(MultipartUpload upload, ObjectKey source, int partNumber, long firstByte, long lastByte, CancellationToken cancellationToken = default);
    ValueTask CompleteMultipartUploadAsync(MultipartUpload upload, IReadOnlyList<MultipartPart> parts, CancellationToken cancellationToken = default);
    ValueTask AbortMultipartUploadAsync(MultipartUpload upload, CancellationToken cancellationToken = default);
}

public sealed record UploadGrantDescriptor(ObjectKey StagingKey, ContentHash ExpectedDigest, long ExpectedLength, DateTimeOffset ExpiresAt);
public interface IPresigningStore
{
    ValueTask<Uri> CreateUploadUriAsync(UploadGrantDescriptor descriptor, CancellationToken cancellationToken = default);
}
public interface IUploadHeaderProvider
{
    IReadOnlyDictionary<string, string> GetRequiredUploadHeaders(UploadGrantDescriptor descriptor);
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
    public string KeyManifestTemplate { get; init; } = "keys.json";
    public string RevocationTemplate { get; init; } = "products/{productId}/revocations.json";
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

    public void ValidateAgainst(Uri repositoryBaseUrl)
    {
        if (!repositoryBaseUrl.IsAbsoluteUri || repositoryBaseUrl.Scheme is not ("http" or "https" or "file" or "s3" or "ftp")) throw new FormatException("Repository base URL must be an absolute http(s), file, S3, or FTP URI.");
        foreach (var baseUrl in BlobBaseUrls)
        {
            if (!baseUrl.IsAbsoluteUri || baseUrl.UserInfo.Length != 0 || baseUrl.Scheme is not ("http" or "https" or "s3" or "ftp")) throw new FormatException("Blob base URLs must be absolute same-origin URLs without credentials.");
            if (!string.Equals(baseUrl.Scheme, repositoryBaseUrl.Scheme, StringComparison.OrdinalIgnoreCase) || !string.Equals(baseUrl.Host, repositoryBaseUrl.Host, StringComparison.OrdinalIgnoreCase) || baseUrl.Port != repositoryBaseUrl.Port)
                throw new FormatException($"Blob base URL '{baseUrl}' is not same-origin with '{repositoryBaseUrl}'.");
            if (baseUrl.AbsolutePath.Contains("%", StringComparison.Ordinal) || baseUrl.AbsolutePath.Split('/').Any(x => x is "." or "..")) throw new FormatException("Blob base URL contains unsafe path syntax.");
        }
    }
}

public sealed record TrustedKey
{
    public required string KeyId { get; init; }
    public required string Algorithm { get; init; }
    public required string PublicKey { get; init; }
    public DateTimeOffset? NotBefore { get; init; }
    public DateTimeOffset? NotAfter { get; init; }
}

public sealed class RepositoryLayout
{
    private readonly RepositoryLayoutTemplates _templates;

    public RepositoryLayout(RepositoryLayoutTemplates templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ValidateTemplate(templates.BlobTemplate, "alg", "h0", "h2", "hash");
        ValidateTemplate(templates.PackageTemplate, "packageId", "version");
        ValidateTemplate(templates.PackageIndexTemplate, "packageId", "page");
        ValidateTemplate(templates.ChannelTemplate, "productId", "channel");
        ValidateTemplate(templates.ReleaseTemplate, "productId", "releaseId");
        ValidateTemplate(templates.ReleaseBundleTemplate, "productId", "releaseId");
        ValidateTemplate(templates.CoverageTemplate, "productId", "releaseId");
        ValidateTemplate(templates.ReleaseIndexTemplate, "productId", "page");
        ValidateTemplate(templates.KeyManifestTemplate);
        ValidateTemplate(templates.RevocationTemplate, "productId");
        _templates = templates;
    }

    public RepositoryLayoutTemplates Templates => _templates;

    public ObjectKey Blob(ContentHash hash) => new(Format(_templates.BlobTemplate, ("alg", Algorithm(hash.Algorithm)), ("h0", Hex(hash)[..2]), ("h2", Hex(hash)[2..4]), ("hash", Hex(hash))));
    public ObjectKey Package(PackageId id, PackageVersion version) => new(Format(_templates.PackageTemplate, ("packageId", id.Value), ("version", version.Label)));
    public ObjectKey PackageIndex(PackageId id, int page = 0) => new(Format(_templates.PackageIndexTemplate, ("packageId", id.Value), ("page", page.ToString(CultureInfo.InvariantCulture))));
    public ObjectKey Channel(string productId, string channel) => new(Format(_templates.ChannelTemplate, ("productId", productId), ("channel", channel)));
    public ObjectKey Release(string productId, string releaseId) => new(Format(_templates.ReleaseTemplate, ("productId", productId), ("releaseId", releaseId)));
    public ObjectKey ReleaseBundle(string productId, string releaseId) => new(Format(_templates.ReleaseBundleTemplate, ("productId", productId), ("releaseId", releaseId)));
    public ObjectKey Coverage(string productId, string releaseId) => new(Format(_templates.CoverageTemplate, ("productId", productId), ("releaseId", releaseId)));
    public ObjectKey ReleaseIndex(string productId, int page = 0) => new(Format(_templates.ReleaseIndexTemplate, ("productId", productId), ("page", page.ToString(CultureInfo.InvariantCulture))));
    public ObjectKey KeyManifest() => new(Format(_templates.KeyManifestTemplate));
    public ObjectKey Revocations(string productId) => new(Format(_templates.RevocationTemplate, ("productId", productId)));

    private static string Format(string template, params (string Name, string Value)[] values)
    {
        var result = template;
        foreach (var (name, value) in values)
        {
            ValidateSegmentValue(value, name);
            result = result.Replace("{" + name + "}", value, StringComparison.Ordinal).Replace("{" + name + ":2}", value, StringComparison.Ordinal).Replace("{" + name + ":4}", value, StringComparison.Ordinal);
        }
        ValidateRelativePath(result, "Repository layout result");
        return result.ToLowerInvariant();
    }

    private static void ValidateTemplate(string template, params string[] allowedNames)
    {
        ValidateRelativePath(template, "Repository layout template", allowPlaceholders: true, allowedNames);
    }

    private static void ValidateRelativePath(string? path, string description, bool allowPlaceholders = false, IReadOnlyCollection<string>? allowedNames = null)
    {
        var literal = allowPlaceholders ? RemovePlaceholders(path ?? string.Empty) : path ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || literal.Contains('\\') || literal.Contains('%') || literal.Contains('?') || literal.Contains('#') || literal.Contains(':') || literal.Any(char.IsControl))
            throw new FormatException($"{description} must be a safe same-origin relative path.");

        if (allowPlaceholders)
        {
            for (var start = path.IndexOf('{'); start >= 0; start = path.IndexOf('{', start))
            {
                var end = path.IndexOf('}', start + 1);
                if (end < 0) throw new FormatException($"{description} contains an unterminated placeholder.");
                var placeholder = path[(start + 1)..end];
                var separator = placeholder.IndexOf(':');
                var name = separator < 0 ? placeholder : placeholder[..separator];
                var width = separator < 0 ? null : placeholder[(separator + 1)..];
                if (allowedNames is null || !allowedNames.Contains(name, StringComparer.Ordinal) || (width is not null && width is not ("2" or "4")))
                    throw new FormatException($"{description} contains an unsupported placeholder '{placeholder}'.");
                start = end + 1;
            }
            var depth = 0;
            foreach (var character in path)
            {
                if (character == '{') depth++;
                else if (character == '}' && --depth < 0) throw new FormatException($"{description} contains an unmatched closing brace.");
            }
            if (depth != 0) throw new FormatException($"{description} contains an unmatched opening brace.");
        }
        else if (path.Contains('{') || path.Contains('}')) throw new FormatException($"{description} contains an unsupported placeholder.");

        foreach (var segment in path.Split('/'))
            if (segment.Length == 0 || segment is "." or ".." || segment.Contains("..", StringComparison.Ordinal))
                throw new FormatException($"{description} contains a traversal segment.");
    }

    private static string RemovePlaceholders(string path)
    {
        var builder = new StringBuilder(path.Length);
        for (var index = 0; index < path.Length; index++)
        {
            if (path[index] != '{') { builder.Append(path[index]); continue; }
            var end = path.IndexOf('}', index + 1);
            if (end < 0) { builder.Append(path[index]); continue; }
            index = end;
        }
        return builder.ToString();
    }

    private static void ValidateSegmentValue(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('/') || value.Contains('\\') || value.Contains('%') || value.Contains('?') || value.Contains('#') || value.Contains(':') || value.Contains('{') || value.Contains('}') || value.Any(char.IsControl) || value is "." or ".." || value.Contains("..", StringComparison.Ordinal))
            throw new FormatException($"Repository layout value '{name}' is not a safe path segment.");
        var maxLength = name is "version" ? 64 : name is "channel" ? 64 : 128;
        if (name is "packageId" or "productId" or "channel" or "releaseId" or "version" && !Identifier.IsValid(value, maxLength))
            throw new FormatException($"Repository layout value '{name}' is not a valid lowercase identifier.");
    }

    private static string Hex(ContentHash hash) => Convert.ToHexString(hash.Value.Span).ToLowerInvariant();
    private static string Algorithm(HashAlgorithmId algorithm) => algorithm switch { HashAlgorithmId.Sha256 => "sha256", HashAlgorithmId.Sha512 => "sha512", HashAlgorithmId.Md5 => "md5", HashAlgorithmId.Blake3 => "blake3", _ => throw new ArgumentOutOfRangeException(nameof(algorithm)) };
}
