using Amazon.S3;
using Amazon.S3.Model;
using FourSaas.AutoUpdater.Core;
using FourSaas.AutoUpdater.Storage;

using System.Globalization;

namespace FourSaas.AutoUpdater.Storage.S3;

public enum S3ProviderProfile
{
    Aws,
    Minio,
    R2,
    B2,
    Generic
}

public class S3ObjectStore : IDelimitedObjectStore, IRangeReadableObjectStore, IServerSideCopyStore, IServerSideTransferStore, IMultipartUploadStore, IMultipartGrantStore, IMultipartGarbageCollector, IPresigningStore, IUploadHeaderProvider, IUploadIntegrityEnforcement, IServerSideVerifier
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly string? _providerIdentity;
    private readonly bool _ownsClient;
    private readonly S3ProviderProfile _providerProfile;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _copyLocks = new(StringComparer.Ordinal);
    public S3ObjectStore(string bucket, string prefix = "", IAmazonS3? client = null, Uri? serviceUrl = null, string? accessKey = null, string? secretKey = null, S3ProviderProfile providerProfile = S3ProviderProfile.Aws)
    {
        _bucket = bucket; _prefix = prefix.Trim('/');
        _providerProfile = providerProfile;
        // A server-side copy is safe across separate store instances only when
        // both endpoint and explicitly configured credential identity agree. For
        // opaque/default credential chains, reference equality is required below.
        _providerIdentity = serviceUrl is null || string.IsNullOrWhiteSpace(accessKey)
            ? null
            : serviceUrl.GetLeftPart(UriPartial.Authority).TrimEnd('/').ToLowerInvariant() + "|" + accessKey;
        if (client is not null) _client = client;
        else
        {
            var config = new AmazonS3Config { ServiceURL = serviceUrl?.ToString(), ForcePathStyle = serviceUrl is not null };
            _client = string.IsNullOrEmpty(accessKey) ? new AmazonS3Client(config) : new AmazonS3Client(accessKey, secretKey, config);
            _ownsClient = true;
        }
    }
    public S3ProviderProfile ProviderProfile => _providerProfile;
    protected bool SupportsConditionalWrites => _providerProfile is S3ProviderProfile.Aws or S3ProviderProfile.Minio or S3ProviderProfile.R2;
    public bool UploadDigestIsStorageEnforced => SupportsConditionalWrites;
    public virtual StorageCapabilities Capabilities => StorageCapabilities.Read | StorageCapabilities.Range | StorageCapabilities.List | StorageCapabilities.Write | StorageCapabilities.ServerSideCopy | StorageCapabilities.Presigning | StorageCapabilities.Multipart | StorageCapabilities.Delete;
    public int RecommendedParallelism => 32;
    public async ValueTask<ReadResult?> OpenAsync(ObjectKey key, long offset = 0, ObjectValidator? ifMatch = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new GetObjectRequest { BucketName = _bucket, Key = FullKey(key), ByteRange = offset > 0 ? new ByteRange(offset, long.MaxValue) : null };
            if (ifMatch is not null) request.EtagToMatch = ifMatch.Value;
            var response = await _client.GetObjectAsync(request, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(response.Headers.ContentEncoding)) { response.Dispose(); throw new InvalidDataException($"Object '{key}' was served with forbidden Content-Encoding '{response.Headers.ContentEncoding}'."); }
            if (offset > 0)
            {
                var contentRange = response.ContentRange;
                if (response.HttpStatusCode != System.Net.HttpStatusCode.PartialContent || string.IsNullOrWhiteSpace(contentRange) || !contentRange.StartsWith($"bytes {offset}-", StringComparison.Ordinal))
                {
                    response.Dispose();
                    return await OpenAsync(key, 0, null, cancellationToken).ConfigureAwait(false);
                }
            }
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
        try { var response = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = FullKey(key) }, cancellationToken).ConfigureAwait(false); if (!string.IsNullOrEmpty(response.Headers.ContentEncoding)) throw new InvalidDataException($"Object '{key}' was served with forbidden Content-Encoding '{response.Headers.ContentEncoding}'."); return new ObjectHead(response.ContentLength, new(ObjectValidatorKind.ETag, response.ETag), null, null, true, response.LastModified.ToUniversalTime(), response.Headers.CacheControl); }
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
    public async IAsyncEnumerable<ObjectListing> ListAsync(string? prefix, string delimiter, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(delimiter)) throw new ArgumentException("A delimiter is required.", nameof(delimiter));
        string? token = null;
        do
        {
            var listPrefix = string.IsNullOrEmpty(_prefix) ? prefix ?? string.Empty : _prefix + "/" + (prefix ?? string.Empty);
            var response = await _client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, Prefix = listPrefix, Delimiter = delimiter, ContinuationToken = token }, cancellationToken).ConfigureAwait(false);
            foreach (var common in response.CommonPrefixes.OrderBy(static x => x, StringComparer.Ordinal))
                yield return new ObjectListing(null, common[_prefix.Length..].TrimStart('/'));
            foreach (var item in response.S3Objects.OrderBy(static x => x.Key, StringComparer.Ordinal))
                yield return new ObjectListing(new ObjectKey(item.Key[_prefix.Length..].TrimStart('/')), null);
            token = response.IsTruncated ? response.NextContinuationToken : null;
        } while (token is not null);
    }
    public async ValueTask PutAsync(ObjectKey key, Stream content, long? length = null, CancellationToken cancellationToken = default)
    {
        var request = new PutObjectRequest { BucketName = _bucket, Key = FullKey(key), InputStream = content, AutoCloseStream = false };
        if (length is { } declared) request.Headers.ContentLength = declared;
        request.Headers["Cache-Control"] = CacheControlFor(key);
        await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public virtual async ValueTask<bool> PutIfAbsentAsync(ObjectKey key, Stream content, long? length = null, CancellationToken cancellationToken = default)
    {
        EnsureConditionalWrites();
        var request = new PutObjectRequest { BucketName = _bucket, Key = FullKey(key), InputStream = content, AutoCloseStream = false };
        if (length is { } declared) request.Headers.ContentLength = declared;
        request.Headers["Cache-Control"] = CacheControlFor(key);
        request.Headers["If-None-Match"] = "*";
        try { await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false); return true; }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed || ex.StatusCode == System.Net.HttpStatusCode.Conflict) { return false; }
    }

    public virtual async ValueTask<bool> CompareAndSwapAsync(ObjectKey key, ObjectValidator expected, Stream content, long? length = null, CancellationToken cancellationToken = default)
    {
        EnsureConditionalWrites();
        if (expected.Kind != ObjectValidatorKind.ETag || !expected.IsStrong) return false;
        var request = new PutObjectRequest { BucketName = _bucket, Key = FullKey(key), InputStream = content, AutoCloseStream = false };
        if (length is { } declared) request.Headers.ContentLength = declared;
        request.Headers["Cache-Control"] = CacheControlFor(key);
        request.Headers["If-Match"] = expected.Value;
        try { await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false); return true; }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed || ex.StatusCode == System.Net.HttpStatusCode.Conflict) { return false; }
    }

    public virtual async ValueTask<bool> PutIfAbsentAsync(ObjectKey key, ContentHash expectedDigest, Stream content, long? length = null, CancellationToken cancellationToken = default)
    {
        EnsureConditionalWrites();
        if (expectedDigest.Algorithm != HashAlgorithmId.Sha256) throw new NotSupportedException("S3 storage-enforced CAS requires sha256.");
        var expectedHex = Convert.ToHexString(expectedDigest.Span).ToLowerInvariant();
        if (!string.Equals(key.Value.Split('/').Last(), expectedHex, StringComparison.Ordinal)) throw new ArgumentException("CAS key must end in the expected sha256 digest.", nameof(key));
        if (!content.CanSeek)
        {
            // A grant may arrive as a network stream and can be many gigabytes.
            // Spooling it to a bounded temporary file preserves the mandatory
            // verify-before-upload behavior without making the publisher's RAM
            // usage proportional to the object size.
            var temporaryPath = Path.Combine(Path.GetTempPath(), "4sup-s3-cas-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            // The spool was described as bounded but had no bound: CopyToAsync ran to EOF, so
            // a stream that never ends filled the disk. The declared length is the bound when
            // the caller supplies one; otherwise the single-PUT limit applies, since anything
            // larger cannot be written by this path anyway.
            var spoolLimit = length ?? 5L * 1024 * 1024 * 1024;
            try
            {
                await using (var buffered = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[1024 * 1024];
                    var remaining = spoolLimit;
                    while (true)
                    {
                        var wanted = (int)Math.Min(buffer.Length, remaining + 1);
                        if (wanted <= 0) break;
                        var read = await content.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
                        if (read == 0) break;
                        if (read > remaining) throw new InvalidDataException($"Content for '{key}' exceeds the declared length of {spoolLimit} bytes.");
                        await buffered.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        remaining -= read;
                    }
                    await buffered.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                await using var replay = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                return await PutIfAbsentAsync(key, expectedDigest, replay, replay.Length, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try { File.Delete(temporaryPath); } catch (IOException) { }
            }
        }
        var original = content.Position;
        var computed = await ContentHash.ComputeAsync(content, HashAlgorithmId.Sha256, cancellationToken).ConfigureAwait(false);
        if (computed != expectedDigest) throw new CryptographicException($"Content does not match CAS digest '{expectedDigest}'.");
        content.Position = original;
        var request = new PutObjectRequest { BucketName = _bucket, Key = FullKey(key), InputStream = content, AutoCloseStream = false, ChecksumAlgorithm = ChecksumAlgorithm.SHA256, ChecksumSHA256 = Convert.ToBase64String(expectedDigest.Value.ToArray()) };
        if (length is { } declared) request.Headers.ContentLength = declared;
        request.Headers["Cache-Control"] = CacheControlFor(key);
        if (SupportsConditionalWrites) request.Headers["If-None-Match"] = "*";
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
        if (UploadDigestIsStorageEnforced)
        {
            request.Headers["x-amz-checksum-sha256"] = Convert.ToBase64String(descriptor.ExpectedDigest.Value.ToArray());
            request.Headers["If-None-Match"] = "*";
        }
        request.Headers["Content-Length"] = descriptor.ExpectedLength.ToString(CultureInfo.InvariantCulture);
        return ValueTask.FromResult(new Uri(_client.GetPreSignedURL(request), UriKind.Absolute));
    }
    public IReadOnlyDictionary<string, string> GetRequiredUploadHeaders(UploadGrantDescriptor descriptor)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Length"] = descriptor.ExpectedLength.ToString(CultureInfo.InvariantCulture)
        };
        if (UploadDigestIsStorageEnforced)
        {
            headers["x-amz-checksum-sha256"] = Convert.ToBase64String(descriptor.ExpectedDigest.Value.ToArray());
            headers["If-None-Match"] = "*";
        }
        return headers;
    }
    public async ValueTask DeleteAsync(ObjectKey key, CancellationToken cancellationToken = default) => await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = FullKey(key) }, cancellationToken).ConfigureAwait(false);
    public async ValueTask CopyAsync(ObjectKey source, ObjectKey destination, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        var destinationKey = FullKey(destination);
        var gate = _copyLocks.GetOrAdd(destinationKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!overwrite && await HeadAsync(destination, cancellationToken).ConfigureAwait(false) is not null) throw new IOException($"Destination '{destination}' already exists.");
            var sourceHead = await HeadAsync(source, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException(source.Value);
            var sourceETag = sourceHead.Validator?.Value;
            const long copyObjectLimit = 5L * 1024 * 1024 * 1024;
            if (sourceHead.Length <= copyObjectLimit)
            {
                var copy = new CopyObjectRequest { SourceBucket = _bucket, SourceKey = FullKey(source), DestinationBucket = _bucket, DestinationKey = destinationKey, MetadataDirective = S3MetadataDirective.REPLACE };
                copy.Headers["Cache-Control"] = CacheControlFor(destination);
                copy.Headers["Content-Encoding"] = string.Empty;
                // Pin the exact source generation. Without it the object copied may not be the
                // one whose length and digest were just inspected.
                if (sourceETag is not null) copy.ETagToMatch = sourceETag;
                // The HEAD above is advisory only; a concurrent writer can create the
                // destination between that check and this copy. Where the provider supports
                // conditional writes, make the copy itself refuse to overwrite.
                if (!overwrite && SupportsConditionalWrites) copy.Headers["If-None-Match"] = "*";
                await _client.CopyObjectAsync(copy, cancellationToken).ConfigureAwait(false);
                return;
            }

            var binding = CopySourceBinding(_bucket, FullKey(source), sourceHead);
            var upload = await ResumeOrStartBoundCopyAsync(destination, binding, cancellationToken).ConfigureAwait(false);
            try
            {
                var partsByNumber = (await ListPartsAsync(upload, cancellationToken).ConfigureAwait(false))
                    .ToDictionary(part => part.Number);
                var partSize = Math.Max(upload.PartSize, 16L * 1024 * 1024);
                var partNumber = 1;
                for (long first = 0; first < sourceHead.Length; first += partSize)
                {
                    var last = Math.Min(sourceHead.Length - 1, first + partSize - 1);
                    if (!partsByNumber.ContainsKey(partNumber))
                        partsByNumber[partNumber] = await CopyPartAsync(upload, source, partNumber, first, last, cancellationToken, sourceETag).ConfigureAwait(false);
                    partNumber++;
                }
                await CompleteMultipartUploadAsync(upload, partsByNumber.Values.OrderBy(part => part.Number).ToArray(), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // An abandoned multipart upload is billed until it is aborted, and leaving it
                // behind also leaves parts a later resume might wrongly adopt.
                try { await AbortMultipartUploadAsync(upload, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
                throw;
            }
        }
        finally { ReleaseCopyGate(destinationKey, gate); }
    }
    public async ValueTask<bool> TryCopyFromAsync(IReadableObjectStore sourceStore, ObjectKey source, ObjectKey destination, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        if (sourceStore is not S3ObjectStore remote) return false;
        // CopyObject is provider-specific.  Never assume that two S3-shaped endpoints
        // share an account/region or even implement the same copy API; the caller can
        // fall back to a verified byte transfer when this identity is unknown.
        if (!ReferenceEquals(_client, remote._client) && (_providerIdentity is null || !string.Equals(_providerIdentity, remote._providerIdentity, StringComparison.Ordinal))) return false;
        var destinationKey = FullKey(destination);
        var gate = _copyLocks.GetOrAdd(destinationKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!overwrite && await HeadAsync(destination, cancellationToken).ConfigureAwait(false) is not null) throw new IOException($"Destination '{destination}' already exists.");
            var sourceHead = await remote.HeadAsync(source, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException(source.Value);
            var sourceETag = sourceHead.Validator?.Value;
            const long copyObjectLimit = 5L * 1024 * 1024 * 1024;
            if (sourceHead.Length <= copyObjectLimit)
            {
                var copy = new CopyObjectRequest
                {
                    SourceBucket = remote._bucket,
                    SourceKey = remote.FullKey(source),
                    DestinationBucket = _bucket,
                    DestinationKey = FullKey(destination),
                    MetadataDirective = S3MetadataDirective.REPLACE
                };
                copy.Headers["Cache-Control"] = CacheControlFor(destination);
                copy.Headers["Content-Encoding"] = string.Empty;
                if (sourceETag is not null) copy.ETagToMatch = sourceETag;
                if (!overwrite && SupportsConditionalWrites) copy.Headers["If-None-Match"] = "*";
                await _client.CopyObjectAsync(copy, cancellationToken).ConfigureAwait(false);
                return true;
            }

            var binding = CopySourceBinding(remote._bucket, remote.FullKey(source), sourceHead);
            var upload = await ResumeOrStartBoundCopyAsync(destination, binding, cancellationToken).ConfigureAwait(false);
            try
            {
                var partsByNumber = (await ListPartsAsync(upload, cancellationToken).ConfigureAwait(false))
                    .ToDictionary(part => part.Number);
                var partSize = Math.Max(upload.PartSize, 16L * 1024 * 1024);
                var partNumber = 1;
                for (long first = 0; first < sourceHead.Length; first += partSize)
                {
                    var last = Math.Min(sourceHead.Length - 1, first + partSize - 1);
                    if (!partsByNumber.ContainsKey(partNumber))
                    {
                        var request = new CopyPartRequest
                        {
                            SourceBucket = remote._bucket,
                            SourceKey = remote.FullKey(source),
                            DestinationBucket = _bucket,
                            DestinationKey = FullKey(upload.Key),
                            UploadId = upload.UploadId,
                            PartNumber = partNumber,
                            FirstByte = first,
                            LastByte = last
                        };
                        // Every part is pinned to the same source generation, so a source
                        // mutated part-way through the copy fails the transfer instead of
                        // producing a destination spliced from two generations.
                        if (sourceETag is not null) request.ETagToMatch = [sourceETag];
                        var response = await _client.CopyPartAsync(request, cancellationToken).ConfigureAwait(false);
                        partsByNumber[partNumber] = new MultipartPart(partNumber, response.ETag, last - first + 1);
                    }
                    partNumber++;
                }
                await CompleteMultipartUploadAsync(upload, partsByNumber.Values.OrderBy(part => part.Number).ToArray(), cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch
            {
                try { await AbortMultipartUploadAsync(upload, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
                throw;
            }
        }
        finally { ReleaseCopyGate(destinationKey, gate); }
    }
    /// <summary>
    /// Metadata key recording which exact source generation a copy multipart upload was
    /// started for.
    /// </summary>
    private const string CopySourceBindingMetadata = "x-amz-meta-4sup-copy-source";

    /// <summary>
    /// Finds a resumable copy upload for <paramref name="destination"/> that was started for
    /// this exact source generation, aborting any that was not.
    ///
    /// Resuming purely on destination key is unsafe: an upload left over from a copy of a
    /// different source, a different length or a different part size would have its existing
    /// parts reused, silently assembling an object from two generations. The binding below
    /// makes a mismatched upload unusable rather than merely unlikely to be picked.
    /// </summary>
    private async ValueTask<MultipartUpload> ResumeOrStartBoundCopyAsync(ObjectKey destination, string binding, CancellationToken cancellationToken)
    {
        var existing = await FindIncompleteMultipartUploadAsync(destination, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            string? existingBinding = null;
            try
            {
                var describe = await _client.ListPartsAsync(new ListPartsRequest { BucketName = _bucket, Key = FullKey(destination), UploadId = existing.UploadId }, cancellationToken).ConfigureAwait(false);
                existingBinding = describe.ResponseMetadata?.Metadata is { } metadata && metadata.TryGetValue(CopySourceBindingMetadata, out var value) ? value : null;
            }
            catch (AmazonS3Exception) { }

            if (string.Equals(existingBinding, binding, StringComparison.Ordinal)) return existing;

            // Not ours, or bound to a different generation: abandon it rather than inherit
            // its parts. Leaving it would also accrue storage charges indefinitely.
            try { await AbortMultipartUploadAsync(existing, cancellationToken).ConfigureAwait(false); } catch (AmazonS3Exception) { }
        }

        var partSize = 16L * 1024 * 1024;
        var request = new InitiateMultipartUploadRequest { BucketName = _bucket, Key = FullKey(destination) };
        request.Headers["Cache-Control"] = CacheControlFor(destination);
        request.Headers["Content-Encoding"] = string.Empty;
        request.Metadata.Add(CopySourceBindingMetadata, binding);
        var response = await _client.InitiateMultipartUploadAsync(request, cancellationToken).ConfigureAwait(false);
        return new MultipartUpload(destination, response.UploadId, partSize);
    }

    /// <summary>Identity of a source generation: its ETag and length.</summary>
    private static string CopySourceBinding(string bucket, string key, ObjectHead head)
        => $"{bucket}/{key}|{head.Validator?.Value ?? "?"}|{head.Length}";

    /// <summary>Releases a per-destination copy gate once nothing is waiting on it.</summary>
    private void ReleaseCopyGate(string destinationKey, SemaphoreSlim gate)
    {
        gate.Release();
        // The map previously grew one entry per destination key for the lifetime of the
        // process. Removing an uncontended gate keeps it proportional to concurrent copies.
        if (gate.CurrentCount == 1) _copyLocks.TryRemove(new KeyValuePair<string, SemaphoreSlim>(destinationKey, gate));
    }

    public async ValueTask<MultipartUpload> StartMultipartUploadAsync(ObjectKey key, CancellationToken cancellationToken = default)
    {
        var partSize = 16L * 1024 * 1024;
        var request = new InitiateMultipartUploadRequest { BucketName = _bucket, Key = FullKey(key) };
        request.Headers["Cache-Control"] = CacheControlFor(key);
        request.Headers["Content-Encoding"] = string.Empty;
        var response = await _client.InitiateMultipartUploadAsync(request, cancellationToken).ConfigureAwait(false);
        return new MultipartUpload(key, response.UploadId, partSize);
    }

    public async ValueTask<MultipartUpload?> FindIncompleteMultipartUploadAsync(ObjectKey key, CancellationToken cancellationToken = default)
    {
        string? keyMarker = null;
        string? uploadIdMarker = null;
        MultipartUpload? newest = null;
        DateTimeOffset newestAt = DateTimeOffset.MinValue;
        do
        {
            var page = await _client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest
            {
                BucketName = _bucket,
                Prefix = FullKey(key),
                KeyMarker = keyMarker,
                UploadIdMarker = uploadIdMarker
            }, cancellationToken).ConfigureAwait(false);
            foreach (var candidate in page.MultipartUploads)
            {
                if (!string.Equals(candidate.Key, FullKey(key), StringComparison.Ordinal)) continue;
                var initiated = candidate.Initiated.ToUniversalTime();
                if (newest is null || initiated > newestAt)
                {
                    newest = new MultipartUpload(key, candidate.UploadId, 16L * 1024 * 1024);
                    newestAt = initiated;
                }
            }
            keyMarker = page.IsTruncated ? page.NextKeyMarker : null;
            uploadIdMarker = page.IsTruncated ? page.NextUploadIdMarker : null;
        }
        while (keyMarker is not null);
        return newest;
    }

    public async ValueTask<IReadOnlyList<MultipartPart>> ListPartsAsync(MultipartUpload upload, CancellationToken cancellationToken = default)
    {
        var result = new List<MultipartPart>();
        string? partNumberMarker = null;
        do
        {
            var page = await _client.ListPartsAsync(new ListPartsRequest
            {
                BucketName = _bucket,
                Key = FullKey(upload.Key),
                UploadId = upload.UploadId,
                PartNumberMarker = partNumberMarker
            }, cancellationToken).ConfigureAwait(false);
            result.AddRange(page.Parts.Select(part => new MultipartPart(part.PartNumber, part.ETag, part.Size)));
            partNumberMarker = page.IsTruncated ? page.NextPartNumberMarker.ToString(CultureInfo.InvariantCulture) : null;
        }
        while (partNumberMarker is not null);
        return result;
    }

    public async ValueTask<BrokeredMultipartUpload> CreateBrokeredMultipartUploadAsync(UploadGrantDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (descriptor.ExpectedLength <= 5L * 1024 * 1024 * 1024) throw new ArgumentException("Brokered multipart grants are reserved for objects larger than the S3 single-PUT limit.", nameof(descriptor));

        var partSize = MultipartPartSizeFor(descriptor.ExpectedLength);
        var request = new InitiateMultipartUploadRequest
        {
            BucketName = _bucket,
            Key = FullKey(descriptor.StagingKey)
        };
        request.Headers["Cache-Control"] = CacheControlFor(descriptor.StagingKey);
        var response = await _client.InitiateMultipartUploadAsync(request, cancellationToken).ConfigureAwait(false);
        var upload = new MultipartUpload(descriptor.StagingKey, response.UploadId, partSize);
        var partCount = checked((int)((descriptor.ExpectedLength + partSize - 1) / partSize));
        var parts = ImmutableArray.CreateBuilder<PresignedUploadPart>(partCount);
        try
        {
            for (var partNumber = 1; partNumber <= partCount; partNumber++)
            {
                var url = _client.GetPreSignedURL(new GetPreSignedUrlRequest
                {
                    BucketName = _bucket,
                    Key = FullKey(descriptor.StagingKey),
                    UploadId = response.UploadId,
                    PartNumber = partNumber,
                    Verb = HttpVerb.PUT,
                    Expires = descriptor.ExpiresAt.UtcDateTime
                });
                parts.Add(new PresignedUploadPart(partNumber, new Uri(url, UriKind.Absolute), ImmutableDictionary<string, string>.Empty));
            }
            return new BrokeredMultipartUpload(upload, parts.MoveToImmutable());
        }
        catch
        {
            try { await AbortMultipartUploadAsync(upload, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            throw;
        }
    }

    public ValueTask CompleteBrokeredMultipartUploadAsync(MultipartUpload upload, IReadOnlyList<MultipartPart> parts, CancellationToken cancellationToken = default)
        => CompleteMultipartUploadAsync(upload, parts, cancellationToken);

    public ValueTask<IReadOnlyList<MultipartPart>> ListBrokeredMultipartPartsAsync(MultipartUpload upload, CancellationToken cancellationToken = default)
        => ListPartsAsync(upload, cancellationToken);

    public ValueTask AbortBrokeredMultipartUploadAsync(MultipartUpload upload, CancellationToken cancellationToken = default)
        => AbortMultipartUploadAsync(upload, cancellationToken);

    public async ValueTask<int> AbortIncompleteMultipartUploadsAsync(string prefix, IReadOnlySet<string> preservedPrefixes, bool dryRun = false, CancellationToken cancellationToken = default)
    {
        var fullPrefix = FullKey(new ObjectKey(prefix.TrimEnd('/') + "/x"))[..^1];
        string? keyMarker = null;
        string? uploadIdMarker = null;
        var count = 0;
        do
        {
            var page = await _client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest
            {
                BucketName = _bucket,
                Prefix = fullPrefix,
                KeyMarker = keyMarker,
                UploadIdMarker = uploadIdMarker
            }, cancellationToken).ConfigureAwait(false);
            foreach (var upload in page.MultipartUploads)
            {
                if (preservedPrefixes.Any(preserved => upload.Key.StartsWith(FullKey(new ObjectKey(preserved.TrimEnd('/') + "/x"))[..^1], StringComparison.Ordinal))) continue;
                count++;
                if (!dryRun)
                    await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = _bucket, Key = upload.Key, UploadId = upload.UploadId }, cancellationToken).ConfigureAwait(false);
            }
            keyMarker = page.IsTruncated ? page.NextKeyMarker : null;
            uploadIdMarker = page.IsTruncated ? page.NextUploadIdMarker : null;
        }
        while (keyMarker is not null);
        return count;
    }

    public async ValueTask<MultipartPart> UploadPartAsync(MultipartUpload upload, int partNumber, Stream content, long length, CancellationToken cancellationToken = default)
    {
        if (partNumber is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(partNumber));
        var response = await _client.UploadPartAsync(new UploadPartRequest { BucketName = _bucket, Key = FullKey(upload.Key), UploadId = upload.UploadId, PartNumber = partNumber, InputStream = content, PartSize = length }, cancellationToken).ConfigureAwait(false);
        return new MultipartPart(partNumber, response.ETag, length);
    }

    public ValueTask<MultipartPart> CopyPartAsync(MultipartUpload upload, ObjectKey source, int partNumber, long firstByte, long lastByte, CancellationToken cancellationToken = default)
        => CopyPartAsync(upload, source, partNumber, firstByte, lastByte, cancellationToken, sourceETagToMatch: null);

    /// <param name="sourceETagToMatch">
    /// Pins the copy to one source generation. Without it, a source mutated between parts
    /// yields a destination assembled from more than one generation.
    /// </param>
    public async ValueTask<MultipartPart> CopyPartAsync(MultipartUpload upload, ObjectKey source, int partNumber, long firstByte, long lastByte, CancellationToken cancellationToken, string? sourceETagToMatch)
    {
        var partRequest = new CopyPartRequest { SourceBucket = _bucket, SourceKey = FullKey(source), DestinationBucket = _bucket, DestinationKey = FullKey(upload.Key), UploadId = upload.UploadId, PartNumber = partNumber, FirstByte = firstByte, LastByte = lastByte };
        if (sourceETagToMatch is not null) partRequest.ETagToMatch = [sourceETagToMatch];
        var response = await _client.CopyPartAsync(partRequest, cancellationToken).ConfigureAwait(false);
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
    private static long MultipartPartSizeFor(long length)
    {
        const long minimum = 16L * 1024 * 1024;
        // S3 permits 10,000 parts, but a brokered grant returns a presigned URL per part in a
        // single response. At the protocol maximum that response reaches several megabytes of
        // URLs before any payload moves. Fewer, larger parts keep it to a sane size and stay
        // well inside the 5 GiB per-part limit for any object S3 can hold.
        const long maximumParts = 1_000;
        var required = (length + maximumParts - 1) / maximumParts;
        var rounded = ((required + minimum - 1) / minimum) * minimum;
        return Math.Max(minimum, rounded);
    }
    private static string CacheControlFor(ObjectKey key)
    {
        if (key.Value == "repo.json") return "max-age=300";
        if (key.Value.Contains("/channels/", StringComparison.Ordinal)) return "max-age=30, must-revalidate";
        if (key.Value == "keys.json"
            || key.Value.EndsWith("/revocations.json", StringComparison.Ordinal)
            || key.Value.EndsWith("/index.json", StringComparison.Ordinal)
            || key.Value.Contains("/index.", StringComparison.Ordinal)
            || key.Value.EndsWith("/product.json", StringComparison.Ordinal))
            return "max-age=30, must-revalidate";
        return "public, max-age=31536000, immutable";
    }

    private void EnsureConditionalWrites()
    {
        if (!SupportsConditionalWrites)
            throw new NotSupportedException($"S3 provider profile '{_providerProfile}' does not guarantee conditional writes; use server-verified placement.");
    }
    private sealed class ResponseStream(GetObjectResponse response) : Stream
    {
        private readonly Stream _inner = response.ResponseStream;
        protected override void Dispose(bool disposing) { if (disposing) { _inner.Dispose(); response.Dispose(); } base.Dispose(disposing); }
        public override ValueTask DisposeAsync() { _inner.Dispose(); response.Dispose(); return ValueTask.CompletedTask; }
        public override bool CanRead => _inner.CanRead; public override bool CanSeek => _inner.CanSeek; public override bool CanWrite => false; public override long Length => _inner.Length; public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush(); public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count); public override int Read(Span<byte> buffer) => _inner.Read(buffer); public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.ReadAsync(buffer, offset, count, ct); public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct); public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException(); public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => throw new NotSupportedException(); public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

/// <summary>
/// S3 provider profile with the conditional-write and content-addressed ports
/// enabled.  Keeping this as a distinct type makes capability negotiation
/// truthful for providers such as B2 that expose S3 syntax without a reliable
/// create-if-absent primitive.
/// </summary>
public sealed class S3ConditionalObjectStore : S3ObjectStore, IConditionalWriteStore, IContentAddressedWriteStore
{
    public S3ConditionalObjectStore(string bucket, string prefix = "", IAmazonS3? client = null, Uri? serviceUrl = null, string? accessKey = null, string? secretKey = null, S3ProviderProfile providerProfile = S3ProviderProfile.Aws)
        : base(bucket, prefix, client, serviceUrl, accessKey, secretKey, providerProfile)
    {
        if (!SupportsConditionalWrites) throw new ArgumentException($"Provider profile '{providerProfile}' does not support conditional writes.", nameof(providerProfile));
    }

    public override StorageCapabilities Capabilities => base.Capabilities | StorageCapabilities.ConditionalWrite;
}
