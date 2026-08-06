using System.Net.Http.Headers;
using FourSaas.AutoUpdater.Core;

namespace FourSaas.AutoUpdater.Storage.Brokering;

/// <summary>
/// Client for the management API's brokered publish protocol.  It deliberately has no
/// operation that uploads payload bytes to the API: binary content is sent only to a
/// storage-issued grant URI, while the API receives hashes and small metadata documents.
/// </summary>
public sealed class ManagementApiClient : IAsyncDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly Uri _apiRoot;
    private readonly string? _bearer;

    public ManagementApiClient(Uri apiBase, string? bearerToken = null, HttpClient? client = null)
    {
        if (!apiBase.IsAbsoluteUri || apiBase.Scheme is not ("http" or "https")) throw new ArgumentException("The management API base must be an absolute HTTP(S) URI.", nameof(apiBase));
        if (apiBase.Scheme == "http" && !string.Equals(Environment.GetEnvironmentVariable("FOURSUP_INSECURE_TRANSPORT"), "1", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The management API must use HTTPS; set FOURSUP_INSECURE_TRANSPORT=1 only for local development.");
        _apiRoot = new Uri(apiBase.ToString().TrimEnd('/') + "/api/v1/", UriKind.Absolute);
        _bearer = bearerToken;
        _client = client ?? new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.None });
        _ownsClient = client is null;
    }

    public async ValueTask<PublishSession> OpenSessionAsync(string repository, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, $"repositories/{Segment(repository)}/publish/sessions", null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        return new PublishSession(new GrantId(root.GetProperty("sessionId").GetString()!), new BackendId(root.GetProperty("backendId").GetString()!), root.GetProperty("maxObjects").GetInt32(), root.GetProperty("maxTotalBytes").GetInt64(), root.GetProperty("expiresAt").GetDateTimeOffset());
    }

    public async ValueTask<long> AllocateSequenceAsync(string repository, string scope, string name, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, $"repositories/{Segment(repository)}/sequences/{Segment(scope)}/{Segment(name)}", null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.GetProperty("sequence").GetInt64();
    }

    public async ValueTask<string> CreateReleaseDraftAsync(string productId, byte[] draftBytes, CancellationToken cancellationToken = default)
    {
        using var content = new ByteArrayContent(draftBytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
        using var response = await SendAsync(HttpMethod.Post, $"products/{Segment(productId)}/releases/drafts", content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.GetProperty("draftId").GetString()!;
    }

    public async ValueTask RegisterDraftCoverageAsync(string productId, string draftId, byte[] coverageBytes, CancellationToken cancellationToken = default)
    {
        using var content = new ByteArrayContent(coverageBytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
        using var response = await SendAsync(HttpMethod.Post, $"products/{Segment(productId)}/releases/drafts/{Segment(draftId)}/coverage", content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<ApiUploadGrant>> CreateGrantsAsync(string repository, string sessionId, IEnumerable<(ContentHash Digest, long Length)> items, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { items = items.Select(x => new { sha256 = x.Digest.ToString(), storedLength = x.Length }).ToArray() });
        using var response = await SendAsync(HttpMethod.Post, $"repositories/{Segment(repository)}/publish/sessions/{Segment(sessionId)}/grants", new ByteArrayContent(payload) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } }, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        var result = new List<ApiUploadGrant>();
        foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
        {
            var digest = ContentHash.Parse(item.GetProperty("expectedDigest").GetString()!);
            var headers = item.TryGetProperty("requiredHeaders", out var headerElements) && headerElements.ValueKind == JsonValueKind.Array
                ? headerElements.EnumerateArray().Select(x => new HttpHeaderRequirement(x.GetProperty("name").GetString()!, x.GetProperty("value").GetString()!)).ToImmutableArray()
                : [];
            var multipartParts = item.TryGetProperty("parts", out var partElements) && partElements.ValueKind == JsonValueKind.Array
                ? partElements.EnumerateArray().Select(part => new ApiPresignedPart(
                    part.GetProperty("number").GetInt32(),
                    ParseStorageUri(part.GetProperty("uri").GetString()!),
                    part.TryGetProperty("requiredHeaders", out var partHeaders) && partHeaders.ValueKind == JsonValueKind.Array
                        ? partHeaders.EnumerateArray().Select(x => new HttpHeaderRequirement(x.GetProperty("name").GetString()!, x.GetProperty("value").GetString()!)).ToImmutableArray()
                        : [])).ToImmutableArray()
                : [];
            result.Add(new ApiUploadGrant(
                item.GetProperty("grantId").GetString()!,
                new ObjectKey(item.GetProperty("stagingKey").GetString()!),
                digest,
                item.GetProperty("expectedLength").GetInt64(),
                item.GetProperty("expiresAt").GetDateTimeOffset(),
                ParseUploadUri(item),
                item.TryGetProperty("enforcement", out var enforcement) ? enforcement.GetString() ?? "serverVerified" : "serverVerified", headers,
                item.TryGetProperty("localPath", out var localPath) && localPath.ValueKind == JsonValueKind.String ? localPath.GetString() : null,
                item.TryGetProperty("multipartUploadId", out var uploadId) && uploadId.ValueKind == JsonValueKind.String ? uploadId.GetString() : null,
                item.TryGetProperty("partSize", out var partSize) && partSize.TryGetInt64(out var size) ? size : null,
                multipartParts,
                item.TryGetProperty("completePath", out var completePath) && completePath.ValueKind == JsonValueKind.String ? completePath.GetString() : null,
                item.TryGetProperty("abortPath", out var abortPath) && abortPath.ValueKind == JsonValueKind.String ? abortPath.GetString() : null));
        }
        return result;
    }

    public async ValueTask UploadAsync(ApiUploadGrant grant, Stream content, long length, CancellationToken cancellationToken = default)
    {
        if (DateTimeOffset.UtcNow >= grant.ExpiresAt) throw new InvalidDataException($"Upload grant '{grant.GrantId}' has expired.");
        if (length != grant.ExpectedLength) throw new InvalidDataException($"Upload grant '{grant.GrantId}' expects {grant.ExpectedLength} bytes, received {length}.");
        if (grant.IsMultipart)
        {
            await UploadMultipartAsync(grant, content, length, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (grant.UploadUri is null)
        {
            if (grant.LocalPath is null) throw new IOException($"Grant '{grant.GrantId}' did not provide a direct storage grant; this client cannot safely send payload bytes through the management API.");
            var directory = Path.GetDirectoryName(grant.LocalPath) ?? throw new IOException("Local grant path has no parent directory.");
            Directory.CreateDirectory(directory);
            await using var output = new FileStream(grant.LocalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            if (output.Length != length) throw new InvalidDataException($"Local grant '{grant.GrantId}' received {output.Length} bytes, expected {length}.");
            return;
        }
        using var request = new HttpRequestMessage(HttpMethod.Put, grant.UploadUri)
        {
            Content = new StreamContent(content)
        };
        request.Content.Headers.ContentLength = length;
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        foreach (var header in grant.RequiredHeaders) request.Headers.TryAddWithoutValidation(header.Name, header.Value);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Uploads each part directly to its presigned storage URL and sends only the resulting
    /// ETags to the management API for completion.  The payload never enters the API path.
    /// </summary>
    public async ValueTask UploadMultipartAsync(ApiUploadGrant grant, Stream content, long length, CancellationToken cancellationToken = default)
    {
        if (!grant.IsMultipart || grant.MultipartPartSize is not { } partSize || grant.MultipartParts.IsDefaultOrEmpty || grant.CompletePath is null)
            throw new ArgumentException("The grant is not a complete multipart grant.", nameof(grant));
        if (DateTimeOffset.UtcNow >= grant.ExpiresAt) throw new InvalidDataException($"Upload grant '{grant.GrantId}' has expired.");
        if (length != grant.ExpectedLength) throw new InvalidDataException($"Upload grant '{grant.GrantId}' expects {grant.ExpectedLength} bytes, received {length}.");

        try
        {
            var completed = new List<object>(grant.MultipartParts.Length);
            long remaining = length;
            foreach (var part in grant.MultipartParts.OrderBy(x => x.Number))
            {
                var expectedPartLength = Math.Min(partSize, remaining);
                if (expectedPartLength <= 0) throw new InvalidDataException($"Multipart grant '{grant.GrantId}' contains too many parts.");
                await using var partContent = await ReadExactlyAsync(content, expectedPartLength, cancellationToken).ConfigureAwait(false);
                var etag = await UploadPartAsync(part, partContent, expectedPartLength, cancellationToken).ConfigureAwait(false);
                completed.Add(new { number = part.Number, etag });
                remaining -= expectedPartLength;
            }
            if (remaining != 0 || await HasMoreBytesAsync(content, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException($"Multipart grant '{grant.GrantId}' did not describe exactly the supplied payload.");

            var payload = JsonSerializer.SerializeToUtf8Bytes(new { parts = completed });
            using var response = await SendAsync(HttpMethod.Post, grant.CompletePath, new ByteArrayContent(payload) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } }, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Keep the multipart upload alive.  S3 part numbers are idempotent,
            // so a caller can retry the same grant after a transient failure and
            // overwrite only the affected parts before completing it.  Explicit
            // abort remains available through AbortMultipartGrantAsync and the
            // server's incomplete-upload garbage collector.
            throw;
        }
    }

    public async ValueTask SealAsync(string repository, string sessionId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, $"repositories/{Segment(repository)}/publish/sessions/{Segment(sessionId)}/seal", null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AbortMultipartAsync(ApiUploadGrant grant, CancellationToken cancellationToken = default)
    {
        if (!grant.IsMultipart || grant.AbortPath is null) throw new ArgumentException("The grant does not expose a multipart abort operation.", nameof(grant));
        using var response = await SendAsync(HttpMethod.Post, grant.AbortPath, null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlySet<string>> QueryBlobsAsync(string repository, IEnumerable<ContentHash> hashes, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { sha256 = hashes.Select(x => x.ToString()).ToArray() });
        using var response = await SendAsync(HttpMethod.Post, $"repositories/{Segment(repository)}/blobs/query", new ByteArrayContent(payload) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } }, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.GetProperty("present").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
    }

    public async ValueTask RegisterPackageVersionAsync(string repository, string packageId, byte[] manifestBytes, CancellationToken cancellationToken = default)
    {
        var content = new ByteArrayContent(manifestBytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
        using var response = await SendAsync(HttpMethod.Post, $"repositories/{Segment(repository)}/packages/{Segment(packageId)}/versions", content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RegisterFileTableAsync(string repository, string packageId, string version, FileTableRef table, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = table.Format,
            shardCount = table.ShardCount,
            digest = table.Digest.ToString(),
            shards = table.Shards.OrderBy(x => x.Index).Select(x => new { index = x.Index, digest = x.Digest.ToString(), count = x.Count, size = x.Size }).ToArray()
        }, SignedDocument.JsonOptions);
        using var response = await SendAsync(HttpMethod.Post, $"repositories/{Segment(repository)}/packages/{Segment(packageId)}/versions/{Segment(version)}/files", new ByteArrayContent(payload) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } }, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask PublishPackageVersionAsync(string repository, string packageId, string version, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, $"repositories/{Segment(repository)}/packages/{Segment(packageId)}/versions/{Segment(version)}/publish", null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask PlaceSignedReleaseAsync(string productId, string releaseId, ReadOnlyMemory<byte> envelopeBytes, CancellationToken cancellationToken = default)
        => await PlaceSignedAsync($"products/{Segment(productId)}/releases/{Segment(releaseId)}/lock", envelopeBytes, cancellationToken).ConfigureAwait(false);

    public async ValueTask PlaceSignedChannelAsync(string productId, string channel, ReadOnlyMemory<byte> envelopeBytes, CancellationToken cancellationToken = default)
        => await PlaceSignedAsync($"products/{Segment(productId)}/channels/{Segment(channel)}", envelopeBytes, cancellationToken).ConfigureAwait(false);

    public async ValueTask PlaceSignedRevocationAsync(string productId, string releaseId, ReadOnlyMemory<byte> envelopeBytes, CancellationToken cancellationToken = default)
        => await PlaceSignedAsync(HttpMethod.Post, $"products/{Segment(productId)}/releases/{Segment(releaseId)}/yank", envelopeBytes, cancellationToken).ConfigureAwait(false);

    public async ValueTask PlaceSignedKeyManifestAsync(string repository, ReadOnlyMemory<byte> envelopeBytes, CancellationToken cancellationToken = default)
        => await PlaceSignedAsync(HttpMethod.Put, $"repositories/{Segment(repository)}/keys", envelopeBytes, cancellationToken).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (_ownsClient) _client.Dispose();
        await ValueTask.CompletedTask;
    }

    private async ValueTask PlaceSignedAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        => await PlaceSignedAsync(HttpMethod.Put, path, bytes, cancellationToken).ConfigureAwait(false);

    private async ValueTask PlaceSignedAsync(HttpMethod method, string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(bytes.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await SendAsync(method, path, content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, new Uri(_apiRoot, path)) { Content = content };
        if (!string.IsNullOrWhiteSpace(_bearer)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearer);
        return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new IOException($"Management API returned {(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
    }

    private static string Segment(string value) => Uri.EscapeDataString(value);

    private static Uri? ParseUploadUri(JsonElement item)
    {
        if (!item.TryGetProperty("uploadUri", out var value) || value.ValueKind != JsonValueKind.String) return null;
        return ParseStorageUri(value.GetString()!);
    }

    private static Uri ParseStorageUri(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : throw new FormatException("The management API returned an invalid storage upload URI.");

    private async ValueTask<string> UploadPartAsync(ApiPresignedPart part, Stream content, long length, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, part.Uri) { Content = new StreamContent(content) };
        request.Content.Headers.ContentLength = length;
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        foreach (var header in part.RequiredHeaders) request.Headers.TryAddWithoutValidation(header.Name, header.Value);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.Headers.ETag is { } etag) return etag.Tag;
        if (response.Headers.TryGetValues("ETag", out var values) && values.FirstOrDefault() is { Length: > 0 } value) return value;
        throw new InvalidDataException($"Storage did not return an ETag for multipart part {part.Number}.");
    }

    private static async ValueTask<MemoryStream> ReadExactlyAsync(Stream source, long length, CancellationToken cancellationToken)
    {
        if (length > int.MaxValue) throw new NotSupportedException("A brokered multipart part exceeds the client buffer limit.");
        var result = new MemoryStream((int)length);
        var buffer = new byte[Math.Min(128 * 1024, (int)Math.Max(1, length))];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The multipart payload ended before the granted length.");
            await result.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
        result.Position = 0;
        return result;
    }

    private static async ValueTask<bool> HasMoreBytesAsync(Stream source, CancellationToken cancellationToken)
    {
        var one = new byte[1];
        return await source.ReadAsync(one.AsMemory(), cancellationToken).ConfigureAwait(false) != 0;
    }
}

public sealed record ApiPresignedPart(int Number, Uri Uri, ImmutableArray<HttpHeaderRequirement> RequiredHeaders);
public sealed record ApiUploadGrant(string GrantId, ObjectKey StagingKey, ContentHash ExpectedDigest, long ExpectedLength, DateTimeOffset ExpiresAt, Uri? UploadUri, string Enforcement, ImmutableArray<HttpHeaderRequirement> RequiredHeaders, string? LocalPath, string? MultipartUploadId = null, long? MultipartPartSize = null, ImmutableArray<ApiPresignedPart> MultipartParts = default, string? CompletePath = null, string? AbortPath = null)
{
    public bool IsMultipart => !string.IsNullOrWhiteSpace(MultipartUploadId) || !MultipartParts.IsDefaultOrEmpty;
}
