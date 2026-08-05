namespace FourSaas.AutoUpdater.Storage.Brokering;

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
            await using var copy = new MemoryStream(); await read.Content.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            return ContentHash.Compute(copy.ToArray(), expectedDigest.Algorithm) == expectedDigest;
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
        if (source is IServerSideCopyStore sameStore && ReferenceEquals(source, destination)) { await sameStore.CopyAsync(staged, destinationKey, overwrite: false, cancellationToken).ConfigureAwait(false); return; }
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
    }
}
