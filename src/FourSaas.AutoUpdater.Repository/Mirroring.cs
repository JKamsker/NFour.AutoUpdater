using System.Security.Cryptography;

namespace FourSaas.AutoUpdater.Repository;

public sealed record MirrorResult(int Copied, int Deleted, ImmutableArray<Diagnostic> Diagnostics);

public sealed class MirrorService
{
    public async ValueTask<MirrorResult> MirrorAsync(IListableObjectStore source, IWritableObjectStore destination, bool deleteOrphans = false, string? channel = null, CancellationToken cancellationToken = default)
    {
        if (channel is not null && !Identifier.IsValid(channel, "channel", out var channelError)) throw new FormatException(channelError);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var sourceKeys = deleteOrphans ? new HashSet<ObjectKey>() : null;
        var copied = 0;
        var deleted = 0;
        var selectedKeys = channel is null ? null : await BuildChannelClosureAsync(source, channel, cancellationToken).ConfigureAwait(false);

        await foreach (var key in source.ListAsync(cancellationToken: cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (selectedKeys is not null && !selectedKeys.Contains(key)) continue;
            sourceKeys?.Add(key);
            var immutable = TryParseBlobKey(key, out var expected) || IsImmutableArtifact(key);
            var transferred = false;
            var destinationExisted = false;
            try
            {
                if (destination is IServerSideTransferStore transfer)
                {
                    destinationExisted = await destination.HeadAsync(key, cancellationToken).ConfigureAwait(false) is not null;
                    try { transferred = await transfer.TryCopyFromAsync(source, key, key, overwrite: !immutable, cancellationToken).ConfigureAwait(false); }
                    catch (IOException)
                    {
                        if (!immutable || await destination.HeadAsync(key, cancellationToken).ConfigureAwait(false) is null) throw;
                    }
                }

                if (transferred)
                {
                    if (immutable)
                    {
                        var sourceDigest = await ComputeDigestAsync(source, key, cancellationToken).ConfigureAwait(false);
                        if (expected is { } expectedDigest && sourceDigest != expectedDigest)
                            throw new CryptographicException($"Source blob '{key}' failed its content digest.");
                        var destinationDigest = await ComputeDigestAsync(destination, key, cancellationToken).ConfigureAwait(false);
                        if (sourceDigest != destinationDigest) throw new CryptographicException($"Destination '{key}' differs from the source.");
                    }
                }
                else if (immutable)
                {
                    if (!await PutImmutableStreamingAsync(source, destination, key, expected, cancellationToken).ConfigureAwait(false))
                        diagnostics.Add(new("MIRROR003", DiagnosticSeverity.Error, $"Destination already contains different bytes for immutable object '{key}'."));
                }
                else
                {
                    var body = await source.OpenAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (body is null) { diagnostics.Add(new("MIRROR001", DiagnosticSeverity.Error, $"Source object '{key}' disappeared.")); continue; }
                    await using (body.ConfigureAwait(false)) await destination.PutAsync(key, body.Content, (await source.HeadAsync(key, cancellationToken).ConfigureAwait(false))?.Length, cancellationToken).ConfigureAwait(false);
                }
                copied++;
            }
            catch (CryptographicException ex)
            {
                if (transferred && !destinationExisted) { try { await destination.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { } }
                diagnostics.Add(new("MIRROR002", DiagnosticSeverity.Error, ex.Message));
            }
            catch (FileNotFoundException)
            {
                diagnostics.Add(new("MIRROR001", DiagnosticSeverity.Error, $"Source object '{key}' disappeared."));
            }
        }

        if (deleteOrphans && sourceKeys is not null && destination is IListableObjectStore destinationList)
            await foreach (var key in destinationList.ListAsync(cancellationToken: cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                if (!sourceKeys.Contains(key)) { await destination.DeleteAsync(key, cancellationToken).ConfigureAwait(false); deleted++; }
        return new MirrorResult(copied, deleted, diagnostics.ToImmutable());
    }

    private static async ValueTask<bool> PutImmutableStreamingAsync(IReadableObjectStore source, IWritableObjectStore destination, ObjectKey key, ContentHash? expected, CancellationToken cancellationToken)
    {
        var sourceObject = await source.OpenAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (sourceObject is null) return false;
        await using (sourceObject.ConfigureAwait(false))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var body = new HashingReadStream(sourceObject.Content, hash);
            bool placed;
            if (expected is { } digest && destination is IContentAddressedWriteStore addressed && body.CanSeek && key.Value.StartsWith("blobs/", StringComparison.Ordinal))
                placed = await addressed.PutIfAbsentAsync(key, digest, body, null, cancellationToken).ConfigureAwait(false);
            else if (destination is IConditionalWriteStore conditional)
                placed = await conditional.PutIfAbsentAsync(key, body, null, cancellationToken).ConfigureAwait(false);
            else
            {
                var existing = await destination.HeadAsync(key, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    await body.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
                    var sourceDigest = new ContentHash(HashAlgorithmId.Sha256, hash.GetHashAndReset());
                    return await ComputeDigestAsync(destination, key, cancellationToken).ConfigureAwait(false) == sourceDigest;
                }
                await destination.PutAsync(key, body, null, cancellationToken).ConfigureAwait(false);
                placed = true;
            }

            if (!placed) await body.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
            var sourceHash = new ContentHash(HashAlgorithmId.Sha256, hash.GetHashAndReset());
            if (expected is { } expectedDigest && sourceHash != expectedDigest) throw new CryptographicException($"Source blob '{key}' failed its content digest.");
            var destinationHash = await ComputeDigestAsync(destination, key, cancellationToken).ConfigureAwait(false);
            return placed ? destinationHash == sourceHash : destinationHash == sourceHash;
        }
    }

    private static async ValueTask<ContentHash> ComputeDigestAsync(IReadableObjectStore store, ObjectKey key, CancellationToken cancellationToken)
    {
        var result = await store.OpenAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException(key.Value);
        await using (result.ConfigureAwait(false)) return await ContentHash.ComputeAsync(result.Content, HashAlgorithmId.Sha256, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryParseBlobKey(ObjectKey key, out ContentHash hash)
    {
        var parts = key.Value.Split('/');
        if (parts.Length == 5 && parts[0] == "blobs" && ContentHash.TryParse(parts[1] + ":" + parts[4], out hash)) return true;
        hash = default; return false;
    }

    private static bool IsImmutableArtifact(ObjectKey key)
        => key.Value.EndsWith("/package.json", StringComparison.Ordinal)
        || key.Value.EndsWith("/release.lock.json", StringComparison.Ordinal)
        || key.Value.EndsWith("/release.bundle.json", StringComparison.Ordinal)
        || key.Value.EndsWith("/coverage.json", StringComparison.Ordinal);

    private static bool MatchesChannel(ObjectKey key, string channel)
    {
        var parts = key.Value.Split('/');
        return parts.Length == 4 && parts[0] == "products" && parts[2] == "channels" && string.Equals(Path.GetFileNameWithoutExtension(parts[3]), channel, StringComparison.Ordinal);
    }

    private static async ValueTask<HashSet<ObjectKey>> BuildChannelClosureAsync(IReadableObjectStore source, string channel, CancellationToken cancellationToken)
    {
        var selected = new HashSet<ObjectKey> { new("repo.json") };
        var descriptorResult = await source.OpenAsync(new ObjectKey("repo.json"), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (descriptorResult is null) throw new FileNotFoundException("repo.json");
        RepositoryDescriptor descriptor;
        await using (descriptorResult.ConfigureAwait(false))
        {
            using var bytes = new MemoryStream();
            await descriptorResult.Content.CopyToAsync(bytes, cancellationToken).ConfigureAwait(false);
            descriptor = RepositoryJson.Deserialize<RepositoryDescriptor>(bytes.ToArray());
        }
        var layout = new RepositoryLayout(descriptor.Layout);
        // Trust and revocation documents are part of the verification closure.  A
        // channel mirror without them is not independently verifiable by a client.
        if (await source.HeadAsync(layout.KeyManifest(), cancellationToken).ConfigureAwait(false) is not null)
            selected.Add(layout.KeyManifest());
        var listed = source is IListableObjectStore listable
            ? await CollectKeysAsync(listable, cancellationToken).ConfigureAwait(false)
            : [];
        foreach (var channelKey in listed.Where(key => IsChannelKey(key, channel)))
        {
            selected.Add(channelKey);
            var pointerResult = await source.OpenAsync(channelKey, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (pointerResult is null) continue;
            await using (pointerResult.ConfigureAwait(false))
            {
                var pointerEnvelope = SignedDocument.DeserializeEnvelope(await ReadAllAsync(pointerResult.Content, cancellationToken).ConfigureAwait(false));
                var pointer = SignedDocument.DeserializePayload<ChannelPointer>(Base64Url.Decode(pointerEnvelope.Payload));
                var productKey = layout.Product(pointer.ProductId);
                if (await source.HeadAsync(productKey, cancellationToken).ConfigureAwait(false) is not null)
                    selected.Add(productKey);
                var revocationKey = layout.Revocations(pointer.ProductId);
                if (await source.HeadAsync(revocationKey, cancellationToken).ConfigureAwait(false) is not null)
                    selected.Add(revocationKey);
                var releaseKey = layout.Release(pointer.ProductId, pointer.ReleaseId);
                selected.Add(releaseKey);
                selected.Add(layout.ReleaseBundle(pointer.ProductId, pointer.ReleaseId));
                selected.Add(layout.Coverage(pointer.ProductId, pointer.ReleaseId));
                var releaseResult = await source.OpenAsync(releaseKey, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (releaseResult is null) continue;
                await using (releaseResult.ConfigureAwait(false))
                {
                    var releaseEnvelope = SignedDocument.DeserializeEnvelope(await ReadAllAsync(releaseResult.Content, cancellationToken).ConfigureAwait(false));
                    var release = SignedDocument.DeserializePayload<ReleaseLock>(Base64Url.Decode(releaseEnvelope.Payload));
                    var repository = new StaticRepository(source, descriptor);
                    foreach (var pin in release.Packages)
                    {
                        selected.Add(new ObjectKey(pin.ManifestPath));
                        selected.Add(layout.PackageIndex(pin.Id));
                        var manifest = await repository.GetManifestAsync(pin.Id, pin.Version, pin.ManifestDigest, cancellationToken).ConfigureAwait(false);
                        if (manifest is null) continue;
                        foreach (var shard in manifest.FileTable.Shards) selected.Add(layout.Blob(shard.Digest));
                        await foreach (var entry in repository.ReadFileTableAsync(manifest, cancellationToken).ConfigureAwait(false))
                            if (entry.Kind == FileEntryKind.File) selected.Add(layout.Blob(entry.Content));
                    }
                }
            }
        }
        return selected;
    }

    private static bool IsChannelKey(ObjectKey key, string channel)
    {
        var parts = key.Value.Split('/');
        return parts.Length == 4 && parts[0] == "products" && parts[2] == "channels" && string.Equals(Path.GetFileNameWithoutExtension(parts[3]), channel, StringComparison.Ordinal);
    }

    private static async ValueTask<HashSet<ObjectKey>> CollectKeysAsync(IListableObjectStore source, CancellationToken cancellationToken)
    {
        var keys = new HashSet<ObjectKey>();
        await foreach (var key in source.ListAsync(cancellationToken: cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false)) keys.Add(key);
        return keys;
    }

    private static async ValueTask<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private sealed class HashingReadStream(Stream inner, IncrementalHash hash) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, count); if (read > 0) hash.AppendData(buffer, offset, read); return read; }
        public override int Read(Span<byte> buffer) { var read = inner.Read(buffer); if (read > 0) hash.AppendData(buffer[..read]); return read; }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false); if (read > 0) hash.AppendData(buffer, offset, read); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); if (read > 0) hash.AppendData(buffer[..read].Span); return read; }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() { inner.Dispose(); return ValueTask.CompletedTask; }
        public override bool CanRead => inner.CanRead; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException(); public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new NotSupportedException(); public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
