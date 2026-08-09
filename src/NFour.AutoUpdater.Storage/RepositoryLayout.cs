using NFour.AutoUpdater.Core;

namespace NFour.AutoUpdater.Storage;

/// <summary>Describes whether a presigned upload binds the grant digest at the storage boundary.</summary>
public interface IUploadIntegrityEnforcement
{
    /// <summary>Gets whether storage verifies uploaded bytes against the grant digest.</summary>
    bool UploadDigestIsStorageEnforced { get; }
}

/// <summary>Verifies stored object bytes without downloading them through the caller.</summary>
public interface IServerSideVerifier
{
    /// <summary>Verifies that an object matches the expected content digest.</summary>
    ValueTask<bool> VerifyAsync(ObjectKey key, ContentHash expected, CancellationToken cancellationToken = default);
}

/// <summary>Defines repository-relative object-key templates.</summary>
public sealed record RepositoryLayoutTemplates
{
    /// <summary>Gets the content-addressed blob-key template.</summary>
    public string BlobTemplate { get; init; } = "blobs/{alg}/{h0:2}/{h2:2}/{hash}";
    /// <summary>Gets the package-manifest key template.</summary>
    public string PackageTemplate { get; init; } = "packages/{packageId}/{version}/package.json";
    /// <summary>Gets the paged package-index key template.</summary>
    public string PackageIndexTemplate { get; init; } = "packages/{packageId}/index{page}.json";
    /// <summary>Gets the channel-pointer key template.</summary>
    public string ChannelTemplate { get; init; } = "products/{productId}/channels/{channel}.json";
    /// <summary>Gets the release-lock key template.</summary>
    public string ReleaseTemplate { get; init; } = "products/{productId}/releases/{releaseId}/release.lock.json";
    /// <summary>Gets the release-bundle key template.</summary>
    public string ReleaseBundleTemplate { get; init; } = "products/{productId}/releases/{releaseId}/release.bundle.json";
    /// <summary>Gets the release-coverage key template.</summary>
    public string CoverageTemplate { get; init; } = "products/{productId}/releases/{releaseId}/coverage.json";
    /// <summary>Gets the paged release-index key template.</summary>
    public string ReleaseIndexTemplate { get; init; } = "products/{productId}/releases/index{page}.json";
    /// <summary>Gets the key-manifest object key.</summary>
    public string KeyManifestTemplate { get; init; } = "keys.json";
    /// <summary>Gets the product-revocations key template.</summary>
    public string RevocationTemplate { get; init; } = "products/{productId}/revocations.json";
    /// <summary>Gets the product-descriptor key template.</summary>
    public string ProductTemplate { get; init; } = "products/{productId}/product.json";
}

/// <summary>Small derived product metadata projection; signed release/channel documents remain authoritative.</summary>
public sealed record ProductDescriptor
{
    /// <summary>Gets the document schema version.</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Gets the product identifier.</summary>
    public required string ProductId { get; init; }
    /// <summary>Gets the optional display name.</summary>
    public string? DisplayName { get; init; }
    /// <summary>Gets the optional product icon URI.</summary>
    public Uri? Icon { get; init; }
}

/// <summary>Describes a repository projection and the operations it exposes.</summary>
public sealed record RepositoryDescriptor
{
    /// <summary>Gets the document schema version.</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Gets the repository identifier.</summary>
    public required string RepositoryId { get; init; }
    /// <summary>Gets when this descriptor projection was generated.</summary>
    public required DateTimeOffset GeneratedAt { get; init; }
    /// <summary>Gets the repository object-key templates.</summary>
    public RepositoryLayoutTemplates Layout { get; init; } = new();
    /// <summary>Gets the content hash algorithm used for repository blobs.</summary>
    public string ContentHashAlgorithm { get; init; } = "sha256";
    /// <summary>Gets the advertised storage capabilities.</summary>
    public StorageCapabilities Capabilities { get; init; } = StorageCapabilities.Read | StorageCapabilities.Range;
    /// <summary>Gets the integrity guarantee identifier.</summary>
    public string IntegrityGuarantee { get; init; } = "verified";
    /// <summary>Gets product identifiers published by the repository.</summary>
    public ImmutableArray<string> Products { get; init; } = [];
    /// <summary>Gets explicitly allowed same-origin blob base URIs.</summary>
    public ImmutableArray<Uri> BlobBaseUrls { get; init; } = [];
    /// <summary>Gets the minimum compatible client version.</summary>
    public string? MinimumClientVersion { get; init; }
    /// <summary>Gets verification keys advertised by the repository.</summary>
    public ImmutableArray<TrustedKey> TrustedKeys { get; init; } = [];

    /// <summary>Validates descriptor URIs and origins against the configured repository base.</summary>
    /// <param name="repositoryBaseUrl">The trusted repository base URI.</param>
    public void ValidateAgainst(Uri repositoryBaseUrl)
    {
        if (!repositoryBaseUrl.IsAbsoluteUri || repositoryBaseUrl.Scheme is not ("http" or "https" or "file" or "s3" or "ftp")) throw new FormatException("Repository base URL must be an absolute http(s), file, S3, or FTP URI.");
        foreach (var baseUrl in BlobBaseUrls)
        {
            var allowedScheme = repositoryBaseUrl.Scheme == "http" ? baseUrl.Scheme is "http" or "https" : baseUrl.Scheme == "https";
            if (!baseUrl.IsAbsoluteUri || baseUrl.UserInfo.Length != 0 || !allowedScheme) throw new FormatException("Blob base URLs must use HTTPS (or HTTP only for an HTTP repository) and contain no credentials.");
            if (!string.Equals(baseUrl.Host, repositoryBaseUrl.Host, StringComparison.OrdinalIgnoreCase) || baseUrl.Port != repositoryBaseUrl.Port)
                throw new FormatException("Blob base URLs must be same-origin with the configured repository URL or be supplied through an explicit client allowlist.");
            if (baseUrl.AbsolutePath.Contains("%", StringComparison.Ordinal) || baseUrl.AbsolutePath.Split('/').Any(x => x is "." or "..")) throw new FormatException("Blob base URL contains unsafe path syntax.");
        }
    }
}

/// <summary>Describes a repository-advertised verification key.</summary>
public sealed record TrustedKey
{
    /// <summary>Gets the stable key identifier.</summary>
    public required string KeyId { get; init; }
    /// <summary>Gets the signature algorithm identifier.</summary>
    public required string Algorithm { get; init; }
    /// <summary>Gets the base64url-encoded public-key bytes.</summary>
    public required string PublicKey { get; init; }
    /// <summary>Gets the optional beginning of the validity window.</summary>
    public DateTimeOffset? NotBefore { get; init; }
    /// <summary>Gets the optional end of the validity window.</summary>
    public DateTimeOffset? NotAfter { get; init; }
}

/// <summary>Formats validated repository object keys from layout templates.</summary>
public sealed class RepositoryLayout
{
    private readonly RepositoryLayoutTemplates _templates;

    /// <summary>Initializes and validates a repository layout.</summary>
    /// <param name="templates">The object-key templates.</param>
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
        ValidateTemplate(templates.ProductTemplate, "productId");
        _templates = templates;
    }

    /// <summary>Gets the validated templates.</summary>
    public RepositoryLayoutTemplates Templates => _templates;

    /// <summary>Formats the object key for a content-addressed blob.</summary>
    public ObjectKey Blob(ContentHash hash) => new(Format(_templates.BlobTemplate, ("alg", Algorithm(hash.Algorithm)), ("h0", Hex(hash)[..2]), ("h2", Hex(hash)[2..4]), ("hash", Hex(hash))));
    /// <summary>Formats the object key for a package manifest.</summary>
    public ObjectKey Package(PackageId id, PackageVersion version) => new(Format(_templates.PackageTemplate, ("packageId", id.Value), ("version", version.Label)));
    /// <summary>Formats the object key for a package-index page.</summary>
    public ObjectKey PackageIndex(PackageId id, int page = 0)
        => page == 0 && string.Equals(_templates.PackageIndexTemplate, "packages/{packageId}/index{page}.json", StringComparison.Ordinal)
            ? new($"packages/{id.Value}/index.json")
            : new(Format(_templates.PackageIndexTemplate, ("packageId", id.Value), ("page", string.Equals(_templates.PackageIndexTemplate, "packages/{packageId}/index{page}.json", StringComparison.Ordinal) ? (page == 0 ? string.Empty : "." + (page - 1).ToString(CultureInfo.InvariantCulture)) : page.ToString(CultureInfo.InvariantCulture))));
    /// <summary>Formats the object key for a channel pointer.</summary>
    public ObjectKey Channel(string productId, string channel) => new(Format(_templates.ChannelTemplate, ("productId", productId), ("channel", channel)));
    /// <summary>Formats the object key for a release lock.</summary>
    public ObjectKey Release(string productId, string releaseId) => new(Format(_templates.ReleaseTemplate, ("productId", productId), ("releaseId", releaseId)));
    /// <summary>Formats the object key for a release bundle.</summary>
    public ObjectKey ReleaseBundle(string productId, string releaseId) => new(Format(_templates.ReleaseBundleTemplate, ("productId", productId), ("releaseId", releaseId)));
    /// <summary>Formats the object key for a release coverage document.</summary>
    public ObjectKey Coverage(string productId, string releaseId) => new(Format(_templates.CoverageTemplate, ("productId", productId), ("releaseId", releaseId)));
    /// <summary>Formats the object key for a release-index page.</summary>
    public ObjectKey ReleaseIndex(string productId, int page = 0)
        => page == 0 && string.Equals(_templates.ReleaseIndexTemplate, "products/{productId}/releases/index{page}.json", StringComparison.Ordinal)
            ? new($"products/{productId}/releases/index.json")
            : new(Format(_templates.ReleaseIndexTemplate, ("productId", productId), ("page", string.Equals(_templates.ReleaseIndexTemplate, "products/{productId}/releases/index{page}.json", StringComparison.Ordinal) ? (page == 0 ? string.Empty : "." + (page - 1).ToString(CultureInfo.InvariantCulture)) : page.ToString(CultureInfo.InvariantCulture))));
    /// <summary>Formats the object key for the verification-key manifest.</summary>
    public ObjectKey KeyManifest() => new(Format(_templates.KeyManifestTemplate));
    /// <summary>Formats the object key for product revocations.</summary>
    public ObjectKey Revocations(string productId) => new(Format(_templates.RevocationTemplate, ("productId", productId)));
    /// <summary>Formats the object key for a product descriptor.</summary>
    public ObjectKey Product(string productId) => new(Format(_templates.ProductTemplate, ("productId", productId)));

    private static string Format(string template, params (string Name, string Value)[] values)
    {
        var result = template;
        foreach (var (name, value) in values)
        {
            ValidateSegmentValue(value, name);
            // Only the substituted identifier is case-normalised. Lowercasing the whole
            // formatted path afterwards also rewrote the template's own literal segments, so a
            // custom layout containing any uppercase literal silently addressed a different
            // object on a case-sensitive backend than the one its author wrote down.
            var normalised = value.ToLowerInvariant();
            result = result.Replace("{" + name + "}", normalised, StringComparison.Ordinal).Replace("{" + name + ":2}", normalised, StringComparison.Ordinal).Replace("{" + name + ":4}", normalised, StringComparison.Ordinal);
        }
        ValidateRelativePath(result, "Repository layout result");
        return result;
    }

    private static void ValidateTemplate(string template, params string[] allowedNames)
    {
        ValidateRelativePath(template, "Repository layout template", allowPlaceholders: true, allowedNames);

        // Formatted keys are no longer lowercased wholesale, so an uppercase literal in a
        // custom template would now survive into the object key. Rejecting it here keeps
        // every repository addressable identically on case-sensitive and case-insensitive
        // backends, and reports the problem when the descriptor is validated rather than
        // when a lookup mysteriously misses.
        var literal = RemovePlaceholders(template);
        if (literal.Any(char.IsUpper))
            throw new FormatException("Repository layout template literals must be lowercase.");
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

    private static string Hex(ContentHash hash) => Convert.ToHexString(hash.Span).ToLowerInvariant();
    private static string Algorithm(HashAlgorithmId algorithm) => algorithm switch { HashAlgorithmId.Sha256 => "sha256", HashAlgorithmId.Sha512 => "sha512", HashAlgorithmId.Md5 => "md5", HashAlgorithmId.Blake3 => "blake3", _ => throw new ArgumentOutOfRangeException(nameof(algorithm)) };
}
