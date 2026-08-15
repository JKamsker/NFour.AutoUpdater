namespace NFour.AutoUpdater.Storage.Brokering;

/// <summary>Identifies one upload grant.</summary><param name="Value">The grant identifier.</param>
public readonly record struct GrantId(string Value);
/// <summary>Identifies a configured storage backend.</summary><param name="Value">The backend identifier.</param>
public readonly record struct BackendId(string Value);
/// <summary>Identifies one multipart upload.</summary><param name="Value">The storage upload identifier.</param>
public readonly record struct MultipartUploadId(string Value);
/// <summary>Defines an HTTP header required by a direct upload grant.</summary><param name="Name">The header name.</param><param name="Value">The required value.</param>
public sealed record HttpHeaderRequirement(string Name, string Value);
/// <summary>Describes one presigned multipart upload part.</summary><param name="Number">The one-based part number.</param><param name="Uri">The upload URI.</param><param name="Checksum">The optional required checksum.</param>
public sealed record PresignedPart(int Number, Uri Uri, ContentHash? Checksum);
/// <summary>Describes where an upload grant's digest is enforced.</summary>
public enum IntegrityEnforcement
{
    /// <summary>Storage rejects bytes that do not match the expected digest.</summary>
    StorageEnforced,
    /// <summary>The management service verifies staged bytes before promotion.</summary>
    ServerVerified,
    /// <summary>The backend provides a reduced integrity guarantee.</summary>
    Reduced
}

/// <summary>Defines immutable identity, length, expiration, and enforcement constraints for an upload.</summary>
public abstract record UploadGrant
{
    /// <summary>Gets the grant identifier.</summary>
    public required GrantId GrantId { get; init; }
    /// <summary>Gets the selected storage backend.</summary>
    public required BackendId Backend { get; init; }
    /// <summary>Gets the grant-specific staging object key.</summary>
    public required ObjectKey StagingKey { get; init; }
    /// <summary>Gets the required content digest.</summary>
    public required ContentHash ExpectedDigest { get; init; }
    /// <summary>Gets the required content length.</summary>
    public required long ExpectedLength { get; init; }
    /// <summary>Gets the grant expiration time.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
    /// <summary>Gets where digest integrity is enforced.</summary>
    public required IntegrityEnforcement Enforcement { get; init; }
    /// <summary>Uploads content with one HTTP PUT request.</summary><param name="Uri">The upload URI.</param><param name="RequiredHeaders">Headers required by storage.</param>
    public sealed record HttpPut(Uri Uri, ImmutableArray<HttpHeaderRequirement> RequiredHeaders) : UploadGrant;
    /// <summary>Uploads content through presigned multipart requests.</summary><param name="UploadId">The multipart upload identifier.</param><param name="PartSize">The required non-final part size.</param><param name="Parts">The presigned parts.</param><param name="AbortUri">The optional abort endpoint.</param>
    public sealed record HttpMultipart(MultipartUploadId UploadId, long PartSize, ImmutableArray<PresignedPart> Parts, Uri? AbortUri) : UploadGrant;
    /// <summary>Uploads content with an HTTP multipart form.</summary><param name="Uri">The form endpoint.</param><param name="Fields">Required form fields.</param>
    public sealed record HttpPostForm(Uri Uri, ImmutableArray<KeyValuePair<string, string>> Fields) : UploadGrant;
    /// <summary>Uploads through publisher-held backend credentials.</summary><param name="Key">The staging object key.</param>
    public sealed record PublisherCredentialed(ObjectKey Key) : UploadGrant;
    /// <summary>Writes content to a local staging path.</summary><param name="AbsolutePath">The absolute destination path.</param>
    public sealed record LocalPath(string AbsolutePath) : UploadGrant;
}

/// <summary>Describes the limits and lifetime of a publish session.</summary><param name="SessionId">The session identifier.</param><param name="Backend">The selected backend.</param><param name="MaxObjects">The maximum object count.</param><param name="MaxTotalBytes">The maximum aggregate byte count.</param><param name="ExpiresAt">The expiration time.</param>
public sealed record PublishSession(GrantId SessionId, BackendId Backend, int MaxObjects, long MaxTotalBytes, DateTimeOffset ExpiresAt);
/// <summary>Verifies staged content against a grant's digest and length.</summary>
public interface IStagedObjectVerifier
{
    /// <summary>Verifies one staged object.</summary>
    ValueTask<bool> VerifyAsync(ObjectKey key, ContentHash expectedDigest, long expectedLength, CancellationToken cancellationToken = default);
}
/// <summary>Verifies staged objects by streaming them from readable storage.</summary><param name="store">The staging object store.</param>
public sealed class StagedObjectVerifier(IReadableObjectStore store) : IStagedObjectVerifier
{
    /// <inheritdoc />
    public async ValueTask<bool> VerifyAsync(ObjectKey key, ContentHash expectedDigest, long expectedLength, CancellationToken cancellationToken = default)
    {
        var head = await store.HeadAsync(key, cancellationToken).ConfigureAwait(false);
        if (head is null || head.Length != expectedLength) return false;
        var read = await store.OpenAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (read is null) return false;
        await using (read.ConfigureAwait(false))
        {
            // Hashed as a stream. Buffering the whole staged object first - and then copying
            // it again via ToArray - makes peak memory proportional to the largest object a
            // publisher can upload, which is exactly the quantity this service cannot bound.
            return await ContentHash.ComputeAsync(read.Content, expectedDigest.Algorithm, cancellationToken).ConfigureAwait(false) == expectedDigest;
        }
    }
}

/// <summary>Promotes verified staged content into immutable served storage.</summary><param name="source">The staging store.</param><param name="destination">The served store.</param><param name="verifier">The staged-object verifier.</param>
public sealed class PromotionService(IReadableObjectStore source, IWritableObjectStore destination, IStagedObjectVerifier verifier)
{
    /// <summary>Verifies and atomically promotes one staged object.</summary>
    public async ValueTask PromoteAsync(ObjectKey staged, ObjectKey destinationKey, ContentHash digest, long length, CancellationToken cancellationToken = default)
    {
        if (!await verifier.VerifyAsync(staged, digest, length, cancellationToken).ConfigureAwait(false)) throw new InvalidDataException($"Staged object '{staged}' failed server-side verification.");
        var existing = await destination.HeadAsync(destinationKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (destination is IServerSideVerifier existingVerifier && await existingVerifier.VerifyAsync(destinationKey, digest, cancellationToken).ConfigureAwait(false)) return;
            var existingRead = await destination.OpenAsync(destinationKey, cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException($"Destination '{destinationKey}' disappeared during promotion.");
            await using (existingRead.ConfigureAwait(false))
            {
                var existingHash = await ContentHash.ComputeAsync(existingRead.Content, digest.Algorithm, cancellationToken).ConfigureAwait(false);
                if (existingHash == digest && existing.Length == length) return;
            }
            throw new IOException($"Immutable destination '{destinationKey}' already contains different bytes.");
        }
        if (source is IServerSideCopyStore sameStore && ReferenceEquals(source, destination))
        {
            await sameStore.CopyAsync(staged, destinationKey, overwrite: false, cancellationToken).ConfigureAwait(false);
            await VerifyDestinationAsync(destinationKey, digest, length, cancellationToken).ConfigureAwait(false);
            return;
        }
        var read = await source.OpenAsync(staged, cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException(staged.Value);
        await using (read.ConfigureAwait(false))
        {
            if (destination is IContentAddressedWriteStore addressed)
            {
                if (!await addressed.PutIfAbsentAsync(destinationKey, digest, read.Content, length, cancellationToken).ConfigureAwait(false)) throw new IOException($"Promotion lost the immutable destination race for '{destinationKey}'.");
            }
            else if (destination is IConditionalWriteStore conditional)
            {
                if (!await conditional.PutIfAbsentAsync(destinationKey, read.Content, length, cancellationToken).ConfigureAwait(false)) throw new IOException($"Promotion lost the immutable destination race for '{destinationKey}'.");
            }
            else throw new IOException("Promotion requires an immutable conditional-write destination.");
        }
        await VerifyDestinationAsync(destinationKey, digest, length, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-reads the destination and confirms it holds the promised bytes.
    ///
    /// A successful server-side copy or conditional write reports that the backend accepted
    /// the request, not that the destination now contains the expected content; truncation,
    /// a silently-substituted body or a backend bug would all still report success. Promotion
    /// creates an immutable object that later releases are pinned to, so it ends with a read
    /// barrier rather than the write's own return value.
    /// </summary>
    private async ValueTask VerifyDestinationAsync(ObjectKey destinationKey, ContentHash digest, long length, CancellationToken cancellationToken)
    {
        var head = await destination.HeadAsync(destinationKey, cancellationToken).ConfigureAwait(false)
                   ?? throw new IOException($"Promoted object '{destinationKey}' is not readable after promotion.");
        if (head.Length != length)
            throw new InvalidDataException($"Promoted object '{destinationKey}' is {head.Length} bytes, expected {length}.");

        if (destination is IServerSideVerifier serverSide)
        {
            if (!await serverSide.VerifyAsync(destinationKey, digest, cancellationToken).ConfigureAwait(false))
                throw new CryptographicException($"Promoted object '{destinationKey}' failed its post-promotion digest check.");
            return;
        }

        var read = await destination.OpenAsync(destinationKey, cancellationToken: cancellationToken).ConfigureAwait(false)
                   ?? throw new IOException($"Promoted object '{destinationKey}' is not readable after promotion.");
        await using (read.ConfigureAwait(false))
        {
            if (await ContentHash.ComputeAsync(read.Content, digest.Algorithm, cancellationToken).ConfigureAwait(false) != digest)
                throw new CryptographicException($"Promoted object '{destinationKey}' failed its post-promotion digest check.");
        }
    }
}
