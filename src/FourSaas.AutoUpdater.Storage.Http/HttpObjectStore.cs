using FourSaas.AutoUpdater.Storage;

namespace FourSaas.AutoUpdater.Storage.Http;

public sealed class HttpObjectStore : IReadableObjectStore
{
    private readonly HttpClient _client;
    private readonly Uri _baseUri;
    private readonly bool _ownsClient;

    public HttpObjectStore(Uri baseUri, HttpClient? client = null)
    {
        _baseUri = new Uri(baseUri.ToString().TrimEnd('/') + "/", UriKind.Absolute);
        if (client is null)
        {
            var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.None };
            _client = new HttpClient(handler);
            _ownsClient = true;
        }
        else _client = client;
    }
    public StorageCapabilities Capabilities => StorageCapabilities.Read | StorageCapabilities.Range;
    public int RecommendedParallelism => 32;

    public async ValueTask<ReadResult?> OpenAsync(ObjectKey key, long offset = 0, ObjectValidator? ifMatch = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, key.Value));
        if (offset > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
        if (ifMatch is { IsStrong: true }) request.Headers.IfRange = new System.Net.Http.Headers.RangeConditionHeaderValue(ifMatch.Value);
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) { response.Dispose(); return null; }
        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable) { response.Dispose(); return new ReadResult { Content = Stream.Null, ActualStartOffset = 0, StatusCode = 416, Validator = null }; }
        response.EnsureSuccessStatusCode();
        var encoding = response.Content.Headers.ContentEncoding.FirstOrDefault();
        if (encoding is not null) { response.Dispose(); throw new InvalidDataException($"Blob '{key}' was served with forbidden Content-Encoding '{encoding}'."); }
        var actualStartOffset = 0L;
        if (response.StatusCode == System.Net.HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range is null || range.From is null || range.From.Value != offset)
            {
                response.Dispose();
                throw new InvalidDataException($"Blob '{key}' returned an invalid Content-Range for offset {offset}.");
            }
            actualStartOffset = range.From.Value;
        }
        return new ReadResult
        {
            Content = new ResponseStream(response),
            ActualStartOffset = actualStartOffset,
            StatusCode = (int)response.StatusCode,
            Validator = ReadValidator(response),
            ContentEncoding = null
        };
    }
    public async ValueTask<ObjectHead?> HeadAsync(ObjectKey key, CancellationToken cancellationToken = default)
    {
        using var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, new Uri(_baseUri, key.Value)), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var encoding = response.Content.Headers.ContentEncoding.FirstOrDefault();
        if (encoding is not null) throw new InvalidDataException($"Blob '{key}' was served with forbidden Content-Encoding '{encoding}'.");
        return new ObjectHead(response.Content.Headers.ContentLength ?? -1, ReadValidator(response), null, response.Content.Headers.ContentType?.MediaType, response.Headers.AcceptRanges.Contains("bytes"), response.Content.Headers.LastModified?.ToUniversalTime(), response.Headers.CacheControl?.ToString());
    }
    public async ValueTask DisposeAsync() { if (_ownsClient) _client.Dispose(); await ValueTask.CompletedTask; }
    private static ObjectValidator? ReadValidator(HttpResponseMessage response) => response.Headers.ETag is { } etag ? new(ObjectValidatorKind.ETag, etag.Tag, !etag.IsWeak) : response.Content.Headers.LastModified is { } modified ? new(ObjectValidatorKind.LastModified, modified.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), false) : null;

    private sealed class ResponseStream(HttpResponseMessage response) : Stream
    {
        private readonly Stream _inner = response.Content.ReadAsStream();
        protected override void Dispose(bool disposing) { if (disposing) { _inner.Dispose(); response.Dispose(); } base.Dispose(disposing); }
        public override ValueTask DisposeAsync() { _inner.Dispose(); response.Dispose(); return ValueTask.CompletedTask; }
        public override bool CanRead => _inner.CanRead; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => _inner.Length; public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count); public override int Read(Span<byte> buffer) => _inner.Read(buffer); public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken); public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException(); public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new NotSupportedException(); public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
