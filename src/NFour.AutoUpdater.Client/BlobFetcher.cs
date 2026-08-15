using System.Security.Cryptography;

namespace NFour.AutoUpdater.Client;

/// <summary>Downloads, resumes, bounds, and verifies content-addressed blobs.</summary>
public sealed class BlobFetcher
{
    private const int TransferBufferSize = 128 * 1024;
    private const int MaximumAttempts = 5;
    private const int RetryBaseDelaySeconds = 1;
    private const int SuccessStatusCode = 200;
    private const int PartialContentStatusCode = 206;
    private const int RangeNotSatisfiableStatusCode = 416;

    /// <summary>Downloads or resumes one blob and verifies its length and digest.</summary>
    /// <param name="store">The source object store.</param>
    /// <param name="key">The source object key.</param>
    /// <param name="expected">The required content digest.</param>
    /// <param name="expectedLength">
    /// The manifest-declared length of the blob.  The transfer is bounded by it: a mirror
    /// that streams past the declared length is treated as an integrity failure at the moment
    /// it overruns, rather than after it has filled the disk and the final hash disagrees.
    /// </param>
    /// <param name="stagingPath">The local resumable staging path.</param>
    /// <param name="cancellationToken">Cancels the transfer.</param>
    public async ValueTask<long> FetchAsync(IReadableObjectStore store, ObjectKey key, ContentHash expected, long expectedLength, string stagingPath, CancellationToken cancellationToken = default)
    {
        if (expectedLength < 0) throw new ArgumentOutOfRangeException(nameof(expectedLength));
        Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
        var metadataPath = stagingPath + ".resume.json";

        if (File.Exists(stagingPath) && new FileInfo(stagingPath).Length == expectedLength)
        {
            try
            {
                await using var completeCandidate = File.OpenRead(stagingPath);
                if (await ContentHash.ComputeAsync(completeCandidate, expected.Algorithm, cancellationToken).ConfigureAwait(false) == expected)
                {
                    DeleteIfPresent(metadataPath);
                    return new FileInfo(stagingPath).Length;
                }
            }
            catch (IOException) { }
        }

        var offset = File.Exists(stagingPath) ? new FileInfo(stagingPath).Length : 0;

        // A staged prefix longer than the whole blob cannot be a prefix of it.
        if (offset > expectedLength)
        {
            DeleteIfPresent(stagingPath);
            DeleteIfPresent(metadataPath);
            offset = 0;
        }
        var validator = offset == 0 ? null : ReadValidator(metadataPath, expected);
        if (offset > 0 && validator is not { IsStrong: true })
        {
            DeleteIfPresent(stagingPath);
            DeleteIfPresent(metadataPath);
            offset = 0;
            validator = null;
        }

        Exception? lastFailure = null;
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var response = await store.OpenAsync(key, offset, validator, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Blob '{key}' was not found.");
                var restart = response.StatusCode == RangeNotSatisfiableStatusCode || response.ActualStartOffset != offset || (offset > 0 && response.StatusCode == SuccessStatusCode);
                if (restart)
                {
                    DeleteIfPresent(stagingPath);
                    DeleteIfPresent(metadataPath);
                    offset = 0;
                    validator = null;
                    continue;
                }
                if (offset > 0 && response.StatusCode != PartialContentStatusCode)
                    throw new IOException($"The storage backend did not honor the validated range for '{key}'.");
                if (offset == 0 && response.Validator is { } initialValidator)
                {
                    validator = initialValidator;
                    await WriteValidatorAsync(metadataPath, expected, initialValidator, cancellationToken).ConfigureAwait(false);
                }

                // Hash the bytes as they cross the response/staging boundary. On a
                // resumed transfer the already-staged prefix is fed into the same
                // incremental hash first, so integrity never depends on a second
                // full-file read after the download completes.
                using var hash = IncrementalHash.CreateHash(expected.Algorithm switch
                {
                    HashAlgorithmId.Sha256 => HashAlgorithmName.SHA256,
                    HashAlgorithmId.Sha512 => HashAlgorithmName.SHA512,
                    HashAlgorithmId.Md5 => HashAlgorithmName.MD5,
                    _ => throw new NotSupportedException($"Hash algorithm {expected.Algorithm} is not supported by the downloader.")
                });
                if (offset > 0)
                {
                    await using var prefix = File.OpenRead(stagingPath);
                    var prefixBuffer = new byte[TransferBufferSize];
                    int prefixRead;
                    while ((prefixRead = await prefix.ReadAsync(prefixBuffer, cancellationToken).ConfigureAwait(false)) > 0)
                        hash.AppendData(prefixBuffer, 0, prefixRead);
                }
                await using (var target = new FileStream(stagingPath, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.Read, TransferBufferSize, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await using (var hashedResponse = new HashingWriteStream(target, hash))
                    await CopyBoundedAsync(response.Content, hashedResponse, expectedLength - offset, key, cancellationToken).ConfigureAwait(false);

                offset = new FileInfo(stagingPath).Length;
                if (offset != expectedLength)
                    throw new InvalidDataException($"Blob '{key}' is {offset} bytes but the manifest declares {expectedLength}.");
                var actual = new ContentHash(expected.Algorithm, hash.GetHashAndReset());
                if (actual != expected)
                    throw new InvalidDataException($"Blob '{key}' failed content verification.");
                DeleteIfPresent(metadataPath);
                return offset;
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidDataException) { throw; }
            catch (IOException ex) when (attempt < MaximumAttempts - 1)
            {
                lastFailure = ex;
                offset = File.Exists(stagingPath) ? new FileInfo(stagingPath).Length : 0;
                if (offset == 0 || validator is not { IsStrong: true })
                {
                    DeleteIfPresent(stagingPath);
                    DeleteIfPresent(metadataPath);
                    offset = 0;
                    validator = null;
                }
                await Task.Delay(TimeSpan.FromSeconds(RetryBaseDelaySeconds << attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException($"The storage backend changed or refused the validator for '{key}' while resuming.", lastFailure);
    }

    /// <summary>
    /// Copies at most <paramref name="remaining"/> bytes, failing as soon as the source tries
    /// to send more.  Reading to EOF would let a hostile or broken mirror write unbounded data
    /// into staging before the length or digest check ever runs.
    /// </summary>
    private static async ValueTask CopyBoundedAsync(Stream source, Stream destination, long remaining, ObjectKey key, CancellationToken cancellationToken)
    {
        var buffer = new byte[TransferBufferSize];
        while (true)
        {
            // One byte beyond the budget is requested deliberately: it distinguishes "exactly
            // the declared length" from "more is coming".
            var wanted = (int)Math.Min(buffer.Length, remaining + 1);
            if (wanted <= 0) break;
            var read = await source.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (read > remaining)
                throw new InvalidDataException($"Blob '{key}' streamed more than its manifest-declared length; the mirror is serving invalid content.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    /// <summary>Fetches from ordered mirrors, demoting any source that fails integrity validation.</summary>
    public async ValueTask<long> FetchFromMirrorsAsync(IReadOnlyList<IReadableObjectStore> mirrors, ObjectKey key, ContentHash expected, long expectedLength, string stagingPath, CancellationToken cancellationToken = default, Action<IReadableObjectStore>? demoteMirror = null)
    {
        if (mirrors.Count == 0) throw new ArgumentException("At least one mirror is required.", nameof(mirrors));
        Exception? last = null;
        var integrityFailure = false;
        foreach (var mirror in mirrors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await FetchAsync(mirror, key, expected, expectedLength, stagingPath, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException)
            {
                last = ex;
                integrityFailure |= ex is InvalidDataException or CryptographicException;
                if (ex is InvalidDataException or CryptographicException) demoteMirror?.Invoke(mirror);
                if (ex is InvalidDataException or CryptographicException)
                {
                    TryDelete(stagingPath);
                    TryDelete(stagingPath + ".resume.json");
                }
            }
        }
        if (integrityFailure) throw new InvalidDataException($"All mirrors failed content verification for '{key}'.", last);
        throw new IOException($"All mirrors failed for '{key}'.", last);
    }

    private static ObjectValidator? ReadValidator(string path, ContentHash expected)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var metadata = JsonSerializer.Deserialize<ResumeMetadata>(File.ReadAllBytes(path));
            return metadata is not null && string.Equals(metadata.ExpectedHash, expected.ToString(), StringComparison.Ordinal) && metadata.IsStrong
                ? new ObjectValidator(metadata.Kind, metadata.Value, metadata.IsStrong)
                : null;
        }
        catch (Exception) { return null; }
    }

    private static async ValueTask WriteValidatorAsync(string path, ContentHash expected, ObjectValidator validator, CancellationToken cancellationToken)
    {
        var metadata = new ResumeMetadata(expected.ToString(), validator.Kind, validator.Value, validator.IsStrong);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
        FlushContainingDirectory(path);
    }

    private static void DeleteIfPresent(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    private static void FlushContainingDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            using var directory = new FileStream(Path.GetDirectoryName(path)!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            directory.Flush(flushToDisk: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record ResumeMetadata(string ExpectedHash, ObjectValidatorKind Kind, string Value, bool IsStrong);

    private sealed class HashingWriteStream(Stream inner, IncrementalHash hash) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); if (count > 0) hash.AppendData(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { inner.Write(buffer); if (!buffer.IsEmpty) hash.AppendData(buffer); }
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false); if (count > 0) hash.AppendData(buffer, offset, count); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false); if (!buffer.IsEmpty) hash.AppendData(buffer.Span); }
        protected override void Dispose(bool disposing) { if (disposing) inner.Flush(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => inner.CanWrite; public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush(); public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => inner.SetLength(value); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override int Read(Span<byte> buffer) => throw new NotSupportedException(); public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new NotSupportedException(); public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

