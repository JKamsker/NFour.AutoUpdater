using Amazon.S3;
using Amazon.S3.Model;
using FourSaas.AutoUpdater.Core;
using FourSaas.AutoUpdater.Storage;

namespace FourSaas.AutoUpdater.Storage.S3;

public sealed class S3ObjectStore : IListableObjectStore, IConditionalWriteStore, IContentAddressedWriteStore, IServerSideCopyStore, IMultipartUploadStore, IPresigningStore, IUploadHeaderProvider, IServerSideVerifier
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly bool _ownsClient;
    public S3ObjectStore(string bucket, string prefix = "", IAmazonS3? client = null, Uri? serviceUrl = null, string? accessKey = null, string? secretKey = null)
    {
        _bucket = bucket; _prefix = prefix.Trim('/');
        if (client is not null) _client = client;
        else
        {
            var config = new AmazonS3Config { ServiceURL = serviceUrl?.ToString(), ForcePathStyle = serviceUrl is not null };
            _client = string.IsNullOrEmpty(accessKey) ? new AmazonS3Client(config) : new AmazonS3Client(accessKey, secretKey, config);
            _ownsClient = true;
        }
    }
    public StorageCapabilities Capabilities => StorageCapabilities.Read | StorageCapabilities.Range | StorageCapabilities.List | StorageCapabilities.Write | StorageCapabilities.ConditionalWrite | StorageCapabilities.ServerSideCopy | StorageCapabilities.Presigning | StorageCapabilities.Multipart | StorageCapabilities.Delete;
    public int RecommendedParallelism => 32;
    public async ValueTask<ReadResult?> OpenAsync(ObjectKey key, long offset = 0, ObjectValidator? ifMatch = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new GetObjectRequest { BucketName = _bucket, Key = FullKey(key), ByteRange = offset > 0 ? new ByteRange(offset, long.MaxValue) : null };
            if (ifMatch is not null) request.EtagToMatch = ifMatch.Value;
            var response = await _client.GetObjectAsync(request, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(response.Headers.ContentEncoding)) { response.Dispose(); throw new InvalidDataException($"Object '{key}' was served with forbidden Content-Encoding '{response.Headers.ContentEncoding}'."); }
            return new ReadResult { Content = new ResponseStream(response), ActualStartOffset = offset, StatusCode = offset > 0 ? 206 : 200, Validator = new(ObjectValidatorKind.ETag, response.ETag) };
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed && offset > 0)
        {
            // S3 exposes this validator as If-Match. A failed match means the
            // object changed; restart from zero and let the content hash decide.
            return await OpenAsync(key, 0, null, cancellationToken).ConfigureAwait(false);
        }
    }
    public async ValueTask<ObjectHead?> HeadAsync(ObjectKey key, CancellationToken cancellationToken = default)
    {
        try { var response = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = FullKey(key) }, cancellationToken).ConfigureAwait(false); if (!string.IsNullOrEmpty(response.Headers.ContentEncoding)) throw new InvalidDataException($"Object '{key}' was served with forbidden Content-Encoding '{response.Headers.ContentEncoding}'."); return new ObjectHead(response.ContentLength, new(ObjectValidatorKind.ETag, response.ETag), null, null, true, response.LastModified.ToUniversalTime()); }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }
    public async IAsyncEnumerable<ObjectKey> ListAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? token = null;
        do
        {
            var listPrefix = string.IsNullOrEmpty(_prefix) ? prefix ?? string.Empty : _prefix + "/" + (prefix ?? string.Empty);
            var response = await _client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, Prefix = listPrefix, ContinuationToken = token }, cancellationToken).ConfigureAwait(false);
            foreach (var item in response.S3Objects) yield return new ObjectKey(item.Key[_prefix.Length..].TrimStart('/'));
            token = response.IsTruncated ? response.NextContinuationToken : null;
        } while (token is not null);
    }
    public async ValueTask PutAsync(ObjectKey key, Stream content, long? length = null, CancellationToken cancellationToken = default)
    {
        var request = new PutObjectRequest { BucketName = _bucket, Key = FullKey(key), InputStream = content, AutoCloseStream = false };
        if (length is { } declared) request.Headers.ContentLength = declared;
        await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> PutIfAbsentAsync(ObjectKey key, Stream content, long? length = null, CancellationToken cancellationToken = default)
    {
        var request = new PutObjectRequest { BucketName = _bucket, Key = FullKey(key), InputStream = content, AutoCloseStream = false };
        if (length is { } declared) request.Headers.ContentLength = declared;
        request.Headers["If-None-Match"] = "*";
        try { await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false); return true; }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed || ex.StatusCode == System.Net.HttpStatusCode.Conflict) { return false; }
    }

    public async ValueTask<bool> CompareAndSwapAsync(ObjectKey key, ObjectValidator expected, Stream content, long? length = null, CancellationToken cancellationToken = default)
    {
        if (expected.Kind != ObjectValidatorKind.ETag || !expected.IsStrong) return false;
        var request = new PutObjectRequest { BucketName = _bucket, Key = FullKey(key), InputStream = content, AutoCloseStream = false };
        if (length is { } declared) request.Headers.ContentLength = declared;
        request.Headers["If-Match"] = expected.Value;
        try { await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false); return true; }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed || ex.StatusCode == System.Net.HttpStatusCode.Conflict) { return false; }
    }

    public async ValueTask<bool> PutIfAbsentAsync(ObjectKey key, ContentHash expectedDigest, Stream content, long? length = null, CancellationToken cancellationToken = default)
    {
        if (expectedDigest.Algorithm != HashAlgorithmId.Sha256) throw new NotSupportedException("S3 storage-enforced CAS requires sha256.");
        var expectedHex = Convert.ToHexString(expectedDigest.Value.Span).ToLowerInvariant();
        if (!string.Equals(key.Value.Split('/').Last(), expectedHex, StringComparison.Ordinal)) throw new ArgumentException("CAS key must end in the expected sha256 digest.", nameof(key));
        if (!content.CanSeek)
        {
            await using var buffered = new MemoryStream();
            await content.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
            buffered.Position = 0;
            return await PutIfAbsentAsync(key, expectedDigest, buffered, buffered.Length, cancellationToken).ConfigureAwait(false);
        }
        var original = content.Position;
        var computed = await ContentHash.ComputeAsync(content, HashAlgorithmId.Sha256, cancellationToken).ConfigureAwait(false);
        if (computed != expectedDigest) throw new CryptographicException($"Content does not match CAS digest '{expectedDigest}'.");
        content.Position = original;
        var request = new PutObjectRequest { BucketName = _bucket, Key = FullKey(key), InputStream = content, AutoCloseStream = false, ChecksumAlgorithm = ChecksumAlgorithm.SHA256, ChecksumSHA256 = Convert.ToBase64String(expectedDigest.Value.ToArray()) };
        if (length is { } declared) request.Headers.ContentLength = declared;
        request.Headers["If-None-Match"] = "*";
        try { await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false); return true; }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed || ex.StatusCode == System.Net.HttpStatusCode.Conflict) { return false; }
    }
    public ValueTask<Uri> CreateUploadUriAsync(UploadGrantDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (descriptor.ExpectedDigest.Algorithm != HashAlgorithmId.Sha256) throw new NotSupportedException("S3 presigned integrity enforcement requires a SHA-256 digest.");
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = FullKey(descriptor.StagingKey),
            Verb = HttpVerb.PUT,
            Expires = descriptor.ExpiresAt.UtcDateTime
        };
        request.Headers["x-amz-checksum-sha256"] = Convert.ToBase64String(descriptor.ExpectedDigest.Value.ToArray());
        return ValueTask.FromResult(new Uri(_client.GetPreSignedURL(request), UriKind.Absolute));
    }
    public IReadOnlyDictionary<string, string> GetRequiredUploadHeaders(UploadGrantDescriptor descriptor)
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-amz-checksum-sha256"] = Convert.ToBase64String(descriptor.ExpectedDigest.Value.ToArray()) };
    public async ValueTask DeleteAsync(ObjectKey key, CancellationToken cancellationToken = default) => await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = FullKey(key) }, cancellationToken).ConfigureAwait(false);
    public async ValueTask CopyAsync(ObjectKey source, ObjectKey destination, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        if (!overwrite && await HeadAsync(destination, cancellationToken).ConfigureAwait(false) is not null) throw new IOException($"Destination '{destination}' already exists.");
        var sourceHead = await HeadAsync(source, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException(source.Value);
        const long copyObjectLimit = 5L * 1024 * 1024 * 1024;
        if (sourceHead.Length <= copyObjectLimit)
        {
            await _client.CopyObjectAsync(new CopyObjectRequest { SourceBucket = _bucket, SourceKey = FullKey(source), DestinationBucket = _bucket, DestinationKey = FullKey(destination), MetadataDirective = S3MetadataDirective.REPLACE }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var upload = await StartMultipartUploadAsync(destination, cancellationToken).ConfigureAwait(false);
        var parts = new List<MultipartPart>();
        try
        {
            var partSize = Math.Max(upload.PartSize, 16L * 1024 * 1024);
            var partNumber = 1;
            for (long first = 0; first < sourceHead.Length; first += partSize)
            {
                var last = Math.Min(sourceHead.Length - 1, first + partSize - 1);
                parts.Add(await CopyPartAsync(upload, source, partNumber++, first, last, cancellationToken).ConfigureAwait(false));
            }
            await CompleteMultipartUploadAsync(upload, parts, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { await AbortMultipartUploadAsync(upload, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            throw;
        }
    }
    public async ValueTask<MultipartUpload> StartMultipartUploadAsync(ObjectKey key, CancellationToken cancellationToken = default)
    {
        var response = await _client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = _bucket, Key = FullKey(key), ChecksumAlgorithm = ChecksumAlgorithm.SHA256 }, cancellationToken).ConfigureAwait(false);
        return new MultipartUpload(key, response.UploadId, 16L * 1024 * 1024);
    }

    public async ValueTask<MultipartPart> UploadPartAsync(MultipartUpload upload, int partNumber, Stream content, long length, CancellationToken cancellationToken = default)
    {
        if (partNumber is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(partNumber));
        var response = await _client.UploadPartAsync(new UploadPartRequest { BucketName = _bucket, Key = FullKey(upload.Key), UploadId = upload.UploadId, PartNumber = partNumber, InputStream = content, PartSize = length }, cancellationToken).ConfigureAwait(false);
        return new MultipartPart(partNumber, response.ETag, length);
    }

    public async ValueTask<MultipartPart> CopyPartAsync(MultipartUpload upload, ObjectKey source, int partNumber, long firstByte, long lastByte, CancellationToken cancellationToken = default)
    {
        var response = await _client.CopyPartAsync(new CopyPartRequest { SourceBucket = _bucket, SourceKey = FullKey(source), DestinationBucket = _bucket, DestinationKey = FullKey(upload.Key), UploadId = upload.UploadId, PartNumber = partNumber, FirstByte = firstByte, LastByte = lastByte }, cancellationToken).ConfigureAwait(false);
        return new MultipartPart(partNumber, response.ETag, lastByte - firstByte + 1);
    }

    public async ValueTask CompleteMultipartUploadAsync(MultipartUpload upload, IReadOnlyList<MultipartPart> parts, CancellationToken cancellationToken = default)
    {
        var ordered = parts.OrderBy(x => x.Number).Select(x => new PartETag(x.Number, x.ETag)).ToList();
        await _client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest { BucketName = _bucket, Key = FullKey(upload.Key), UploadId = upload.UploadId, PartETags = ordered }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AbortMultipartUploadAsync(MultipartUpload upload, CancellationToken cancellationToken = default)
        => await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = _bucket, Key = FullKey(upload.Key), UploadId = upload.UploadId }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> VerifyAsync(ObjectKey key, ContentHash expected, CancellationToken cancellationToken = default)
    {
        if (expected.Algorithm != HashAlgorithmId.Sha256) return false;
        var result = await OpenAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result is null) return false;
        await using (result.ConfigureAwait(false))
            return await ContentHash.ComputeAsync(result.Content, HashAlgorithmId.Sha256, cancellationToken).ConfigureAwait(false) == expected;
    }
    public async ValueTask DisposeAsync() { if (_ownsClient) _client.Dispose(); await ValueTask.CompletedTask; }
    private string FullKey(ObjectKey key) => string.IsNullOrEmpty(_prefix) ? key.Value : _prefix + "/" + key.Value;
    private sealed class ResponseStream(GetObjectResponse response) : Stream
    {
        private readonly Stream _inner = response.ResponseStream;
        protected override void Dispose(bool disposing) { if (disposing) { _inner.Dispose(); response.Dispose(); } base.Dispose(disposing); }
        public override ValueTask DisposeAsync() { _inner.Dispose(); response.Dispose(); return ValueTask.CompletedTask; }
        public override bool CanRead => _inner.CanRead; public override bool CanSeek => _inner.CanSeek; public override bool CanWrite => false; public override long Length => _inner.Length; public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush(); public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count); public override int Read(Span<byte> buffer) => _inner.Read(buffer); public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.ReadAsync(buffer, offset, count, ct); public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct); public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException(); public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => throw new NotSupportedException(); public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
