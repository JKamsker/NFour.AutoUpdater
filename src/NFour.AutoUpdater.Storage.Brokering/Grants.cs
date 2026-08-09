namespace NFour.AutoUpdater.Storage.Brokering;

public readonly record struct GrantId(string Value);
public readonly record struct BackendId(string Value);
public readonly record struct MultipartUploadId(string Value);
public sealed record HttpHeaderRequirement(string Name, string Value);
public sealed record PresignedPart(int Number, Uri Uri, ContentHash? Checksum);
public enum IntegrityEnforcement { StorageEnforced, ServerVerified, Reduced }

public abstract record UploadGrant
{
    public required GrantId GrantId { get; init; }
    public required BackendId Backend { get; init; }
    public required ObjectKey StagingKey { get; init; }
    public required ContentHash ExpectedDigest { get; init; }
    public required long ExpectedLength { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required IntegrityEnforcement Enforcement { get; init; }
    public sealed record HttpPut(Uri Uri, ImmutableArray<HttpHeaderRequirement> RequiredHeaders) : UploadGrant;
    public sealed record HttpMultipart(MultipartUploadId UploadId, long PartSize, ImmutableArray<PresignedPart> Parts, Uri? AbortUri) : UploadGrant;
    public sealed record HttpPostForm(Uri Uri, ImmutableArray<KeyValuePair<string, string>> Fields) : UploadGrant;
    public sealed record PublisherCredentialed(ObjectKey Key) : UploadGrant;
    public sealed record LocalPath(string AbsolutePath) : UploadGrant;
}

public sealed record PublishSession(GrantId SessionId, BackendId Backend, int MaxObjects, long MaxTotalBytes, DateTimeOffset ExpiresAt);
public interface IStagedObjectVerifier
{
    ValueTask<bool> VerifyAsync(ObjectKey key, ContentHash expectedDigest, long expectedLength, CancellationToken cancellationToken = default);
}
public sealed class StagedObjectVerifier(IReadableObjectStore store) : IStagedObjectVerifier
{
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

public sealed class PromotionService(IReadableObjectStore source, IWritableObjectStore destination, IStagedObjectVerifier verifier)
{
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
