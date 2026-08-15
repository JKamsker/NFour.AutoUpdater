using NFour.AutoUpdater.Client;
using NFour.AutoUpdater.Core;
using NFour.AutoUpdater.Storage;

namespace NFour.AutoUpdater.Client.Tests;

public sealed class BlobFetcherAdversarialTests
{
    [Fact]
    public async Task StreamingStopsAtFirstBytePastManifestLength()
    {
        var root = Path.Combine(Path.GetTempPath(), "4sup-overrun-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, "blob");
        await using var store = new EndlessObjectStore();
        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => new BlobFetcher()
                .FetchAsync(store, new ObjectKey("blob"), ContentHash.Compute("abc"u8), 3, staging)
                .AsTask());

            Assert.Contains("manifest-declared length", error.Message, StringComparison.Ordinal);
            Assert.Equal(4, store.BytesRead);
            Assert.Equal(0, new FileInfo(staging).Length);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class EndlessObjectStore : IReadableObjectStore
    {
        private readonly EndlessStream _stream = new();

        public long BytesRead => _stream.BytesRead;
        public StorageCapabilities Capabilities => StorageCapabilities.Read;
        public int RecommendedParallelism => 1;

        public ValueTask<ReadResult?> OpenAsync(ObjectKey key, long offset = 0, ObjectValidator? ifMatch = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ReadResult?>(new ReadResult
            {
                Content = _stream,
                ActualStartOffset = offset,
                Validator = null
            });

        public ValueTask<ObjectHead?> HeadAsync(ObjectKey key, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ObjectHead?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EndlessStream : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'x', offset, count);
            BytesRead += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Fill((byte)'x');
            BytesRead += buffer.Length;
            return ValueTask.FromResult(buffer.Length);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
