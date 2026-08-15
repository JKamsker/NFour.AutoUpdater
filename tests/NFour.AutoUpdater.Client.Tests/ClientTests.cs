using NFour.AutoUpdater.Client;
using NFour.AutoUpdater.Core;
using NFour.AutoUpdater.Repository;
using NFour.AutoUpdater.Storage;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace NFour.AutoUpdater.Client.Tests;

public sealed class ClientTests
{
    [Fact]
    public async Task ApplyUsesVerifiedBarrierAndWritesCompactLedger()
    {
        var root = Path.Combine(Path.GetTempPath(), "4sup-apply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var store = new NFour.AutoUpdater.Storage.Memory.MemoryObjectStore();
            var layout = new RepositoryLayout(new RepositoryLayoutTemplates());
            var content = "verified-content"u8.ToArray();
            var hash = ContentHash.Compute(content);
            await store.PutAsync(layout.Blob(hash), new MemoryStream(content), content.Length);
            var path = new VirtualPath("bin/game.exe");
            var owner = new PackageId("core");
            var file = new ComposedFile(path, hash, content.Length, owner, FileInstallPolicy.Replace);
            var files = new Dictionary<VirtualPath, ComposedFile> { [path] = file }.ToImmutableSortedDictionary();
            var target = new ComposedFileSet { Files = files, Shadowed = [], FileSetId = FileSetIdentity.Compute(files) };
            var observed = await new LocalTreeScanner().ScanAsync(root, [], [path], HashPolicy.Never);
            var plan = new InstallPlanner().Plan(target, null, observed);
            var installLock = new InstallLock { RepositoryUri = "memory://test", ProductId = "product", Channel = "live", ReleaseId = "r1", ReleaseDigest = ContentHash.Compute("release"u8), Selection = new VariantSelection { Axes = ImmutableSortedDictionary<string, ImmutableSortedSet<string>>.Empty }, SelectionId = ContentHash.Compute([]), FileSetId = target.FileSetId, AppliedAt = DateTimeOffset.UtcNow };
            var cache = new LocalContentCache(Path.Combine(root, "cache"));
            await cache.StoreAsync(hash, new MemoryStream(content));
            await new InstallApplier().ApplyAsync(root, plan, target, installLock, store, layout, new InstallLedger(root), preconditions: new ApplyPreconditions(), cache: cache);
            Assert.Equal("verified-content", await File.ReadAllTextAsync(Path.Combine(root, "bin", "game.exe")));
            var ledgerText = await File.ReadAllTextAsync(Path.Combine(root, ".4sup", "state.jsonl"));
            Assert.Contains("\"k\":\"lock\"", ledgerText, StringComparison.Ordinal);
            Assert.NotNull(await new InstallLedger(root).ReadAsync());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void FreshPlanNeverDeletesUnknownFiles()
    {
        var path = new VirtualPath("bin/game.exe"); var targetFile = new ComposedFile(path, ContentHash.Compute("game"u8), 4, new PackageId("core"), FileInstallPolicy.Replace);
        var target = new ComposedFileSet { Files = new Dictionary<VirtualPath, ComposedFile> { [path] = targetFile }.ToImmutableSortedDictionary(), Shadowed = [], FileSetId = ContentHash.Compute([]) };
        var observed = new ObservedTreeSnapshot("/install", ImmutableDictionary<VirtualPath, ObservedEntry>.Empty);
        var plan = new InstallPlanner().Plan(target, null, observed);
        Assert.DoesNotContain(plan.Operations, x => x is FileOperation.Delete);
    }

    [Fact]
    public void PreserveFilesBecomeOrphansInsteadOfDeletes()
    {
        var path = new VirtualPath("config/user.ini"); var current = new Dictionary<VirtualPath, InstalledFile> { [path] = new InstalledFile(path, ContentHash.Compute("old"u8), 3, new PackageId("core"), FileInstallPolicy.Preserve, 3, 1) };
        var target = new ComposedFileSet { Files = ImmutableSortedDictionary<VirtualPath, ComposedFile>.Empty, Shadowed = [], FileSetId = ContentHash.Compute([]) };
        var plan = new InstallPlanner().Plan(target, current, new ObservedTreeSnapshot("/install", ImmutableDictionary<VirtualPath, ObservedEntry>.Empty));
        Assert.Contains(plan.Operations, x => x is FileOperation.Orphan);
        Assert.DoesNotContain(plan.Operations, x => x is FileOperation.Delete);
    }

    [Fact]
    public async Task MirrorFailoverDiscardsPoisonedPartialContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "4sup-mirror-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var bad = new NFour.AutoUpdater.Storage.Memory.MemoryObjectStore();
            await using var good = new NFour.AutoUpdater.Storage.Memory.MemoryObjectStore();
            var expectedBytes = "correct"u8.ToArray(); var expected = ContentHash.Compute(expectedBytes); var key = new ObjectKey("blobs/sha256/00/00/" + Convert.ToHexString(expected.Span).ToLowerInvariant());
            await bad.PutAsync(key, new MemoryStream("poisoned"u8.ToArray()));
            await good.PutAsync(key, new MemoryStream(expectedBytes));
            var staging = Path.Combine(root, "staging");
            var length = await new BlobFetcher().FetchFromMirrorsAsync([bad, good], key, expected, expectedBytes.Length, staging);
            Assert.Equal(expectedBytes.Length, length);
            Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(staging));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task BlobFetcherResumesAfterAnInjectedReadFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "4sup-fault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var inner = new NFour.AutoUpdater.Storage.Memory.MemoryObjectStore();
            var bytes = Enumerable.Range(0, 128).Select(x => (byte)x).ToArray();
            var hash = ContentHash.Compute(bytes);
            var key = new ObjectKey("blobs/sha256/00/00/" + Convert.ToHexString(hash.Span).ToLowerInvariant());
            await inner.PutAsync(key, new MemoryStream(bytes, writable: false), bytes.Length);
            await using var faulty = new FaultyObjectStore(inner) { FailAfterBytes = 17 };
            var staging = Path.Combine(root, "blob");
            await Assert.ThrowsAsync<IOException>(() => new BlobFetcher().FetchAsync(faulty, key, hash, bytes.Length, staging).AsTask());
            Assert.InRange(new FileInfo(staging).Length, 17, 85);
            faulty.FailAfterBytes = null;
            Assert.Equal(bytes.Length, await new BlobFetcher().FetchAsync(faulty, key, hash, bytes.Length, staging));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(staging));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LedgerReadDoesNotCreateInstallMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "4sup-readonly-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(await new InstallLedger(root).ReadAsync());
            Assert.False(Directory.Exists(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ApplyRejectsAReparsePointInAManagedParent()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "4sup-reparse-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "4sup-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "bin"), outside);
            await using var store = new NFour.AutoUpdater.Storage.Memory.MemoryObjectStore();
            var layout = new RepositoryLayout(new RepositoryLayoutTemplates());
            var content = "must-not-land-outside"u8.ToArray(); var hash = ContentHash.Compute(content);
            await store.PutAsync(layout.Blob(hash), new MemoryStream(content), content.Length);
            var path = new VirtualPath("bin/game.exe"); var file = new ComposedFile(path, hash, content.Length, new PackageId("core"), FileInstallPolicy.Replace);
            var files = new Dictionary<VirtualPath, ComposedFile> { [path] = file }.ToImmutableSortedDictionary();
            var target = new ComposedFileSet { Files = files, Shadowed = [], FileSetId = FileSetIdentity.Compute(files) };
            var observed = await new LocalTreeScanner().ScanAsync(root, [], [path], HashPolicy.Never);
            var plan = new InstallPlanner().Plan(target, null, observed);
            var installLock = new InstallLock { RepositoryUri = "memory://test", ProductId = "product", Channel = "live", ReleaseId = "r1", ReleaseDigest = ContentHash.Compute("release"u8), Selection = new VariantSelection { Axes = ImmutableSortedDictionary<string, ImmutableSortedSet<string>>.Empty }, SelectionId = ContentHash.Compute([]), FileSetId = target.FileSetId, AppliedAt = DateTimeOffset.UtcNow };
            await Assert.ThrowsAsync<IOException>(async () => await new InstallApplier().ApplyAsync(root, plan, target, installLock, store, layout, new InstallLedger(root), preconditions: new ApplyPreconditions()));
            Assert.False(File.Exists(Path.Combine(outside, "game.exe")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); if (Directory.Exists(outside)) Directory.Delete(outside, true); }
    }

    [Fact]
    public async Task LocallyModifiedFilesAreRewrittenEvenWhenNoHashWasObserved()
    {
        var root = Path.Combine(Path.GetTempPath(), "4sup-nohash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        try
        {
            var path = new VirtualPath("bin/game.exe");
            var wanted = "wanted"u8.ToArray();
            var hash = ContentHash.Compute(wanted);
            var file = new ComposedFile(path, hash, wanted.Length, new PackageId("core"), FileInstallPolicy.Replace);
            var files = new Dictionary<VirtualPath, ComposedFile> { [path] = file }.ToImmutableSortedDictionary();
            var target = new ComposedFileSet { Files = files, Shadowed = [], FileSetId = FileSetIdentity.Compute(files) };

            // The file on disk has been modified locally since it was installed, so its size
            // and mtime no longer match what the ledger recorded.
            var full = Path.Combine(root, "bin", "game.exe");
            await File.WriteAllBytesAsync(full, "locally-tampered"u8.ToArray());

            var ledger = new Dictionary<VirtualPath, InstalledFile>
            {
                [path] = new(path, hash, wanted.Length, new PackageId("core"), FileInstallPolicy.Replace, wanted.Length, 0)
            };

            // HashPolicy.Never observes no content hash at all. The planner must not read that
            // absence as "unchanged" just because the ledger hash still matches the target.
            var observed = await new LocalTreeScanner().ScanAsync(root, ledger.Keys, [path], HashPolicy.Never);
            var plan = new InstallPlanner().Plan(target, ledger, observed);

            Assert.Contains(plan.Operations, op => op is FileOperation.Write write && write.Path == path);
            Assert.DoesNotContain(plan.Operations, op => op is FileOperation.Keep);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CacheMutationIsRejectedBeforeMaterialisation()
    {
        var root = Path.Combine(Path.GetTempPath(), "4sup-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cache = new LocalContentCache(root);
            var hash = ContentHash.Compute("original"u8);
            await cache.StoreAsync(hash, new MemoryStream("original"u8.ToArray()));
            File.SetAttributes(cache.GetPath(hash), FileAttributes.Normal);
            await File.WriteAllBytesAsync(cache.GetPath(hash), "tampered"u8.ToArray());
            Assert.False(await cache.TryGetAsync(hash));
            Assert.False(File.Exists(cache.GetPath(hash)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task NamedPipeIpcSupportsAuthenticatedStreamingFrames()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        var pipe = "4sup-test-" + Guid.NewGuid().ToString("N");
        var router = new IpcRouter().Register("/progress", Stream);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = new NamedPipeIpcServer(pipe, router);
        var serverTask = server.RunAsync(cancellation.Token);
        try
        {
            await using var client = new NamedPipeIpcTransport(pipe);
            using var payload = JsonDocument.Parse("{\"job\":\"demo\"}");
            var frames = new List<string>();
            await foreach (var frame in client.StreamAsync("progress", payload.RootElement.Clone(), cancellation.Token))
                frames.Add(frame.GetProperty("step").GetString()!);
            Assert.Equal(["one", "two"], frames);
        }
        finally
        {
            cancellation.Cancel();
            try { await serverTask; } catch (OperationCanceledException) { }
        }

        static async IAsyncEnumerable<JsonElement> Stream(JsonElement payload, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return JsonSerializer.SerializeToElement(new { step = "one" });
            yield return JsonSerializer.SerializeToElement(new { step = "two" });
        }
    }

    [Fact]
    public async Task LedgerRoundTripPreservesUnknownStateFields()
    {
        var root = Path.Combine(Path.GetTempPath(), "4sup-ledger-fields-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var installLock = new InstallLock
            {
                RepositoryUri = "memory://test",
                ProductId = "product",
                ReleaseId = "r1",
                ReleaseDigest = ContentHash.Compute("release"u8),
                Selection = new VariantSelection { Axes = ImmutableSortedDictionary<string, ImmutableSortedSet<string>>.Empty },
                SelectionId = ContentHash.Compute([]),
                FileSetId = ContentHash.Compute([]),
                AppliedAt = DateTimeOffset.UtcNow
            };
            var ledger = new InstallLedger(root);
            await ledger.CommitAsync(installLock, ImmutableDictionary<VirtualPath, InstalledFile>.Empty);
            var path = Path.Combine(root, ".4sup", "state.jsonl");
            var line = await File.ReadAllTextAsync(path);
            await File.WriteAllTextAsync(path, line.TrimEnd() is { } trimmed && trimmed.EndsWith('}')
                ? trimmed[..^1] + ",\"futureField\":{\"value\":7}}\n"
                : line);

            var loaded = await ledger.ReadAsync();
            Assert.NotNull(loaded);
            Assert.Equal(7, loaded!.Lock.UnknownFields["futureField"].GetProperty("value").GetInt32());
            await ledger.CommitAsync(loaded.Lock, loaded.Files);
            Assert.Contains("futureField", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class FaultyObjectStore(IReadableObjectStore inner) : IReadableObjectStore
    {
        public int? FailAfterBytes { get; set; }
        public bool IgnoreRange { get; init; }
        public StorageCapabilities Capabilities => inner.Capabilities;
        public int RecommendedParallelism => inner.RecommendedParallelism;

        public async ValueTask<ReadResult?> OpenAsync(ObjectKey key, long offset = 0, ObjectValidator? ifMatch = null, CancellationToken cancellationToken = default)
        {
            var result = await inner.OpenAsync(key, IgnoreRange ? 0 : offset, ifMatch, cancellationToken).ConfigureAwait(false);
            if (result is null || FailAfterBytes is null) return result;
            return new ReadResult
            {
                Content = new FaultyStream(result.Content, FailAfterBytes.Value),
                ActualStartOffset = result.ActualStartOffset,
                StatusCode = result.StatusCode,
                Validator = result.Validator,
                ContentEncoding = result.ContentEncoding
            };
        }

        public ValueTask<ObjectHead?> HeadAsync(ObjectKey key, CancellationToken cancellationToken = default) => inner.HeadAsync(key, cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class FaultyStream(Stream inner, int failAfter) : Stream
        {
            private int _read;
            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => inner.Length;
            public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
            public override int Read(Span<byte> buffer)
            {
                if (_read >= failAfter) throw new IOException("Injected read failure.");
                var allowed = Math.Min(buffer.Length, failAfter - _read);
                var read = inner.Read(buffer[..allowed]);
                _read += read;
                return read;
            }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_read >= failAfter) throw new IOException("Injected read failure.");
                var allowed = Math.Min(buffer.Length, failAfter - _read);
                var read = await inner.ReadAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);
                _read += read;
                return read;
            }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
            public override void Flush() => throw new NotSupportedException();
            public override Task FlushAsync(CancellationToken cancellationToken) => Task.FromException(new NotSupportedException());
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException(new NotSupportedException());
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException(new NotSupportedException());
            protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
            public override ValueTask DisposeAsync() { inner.Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
