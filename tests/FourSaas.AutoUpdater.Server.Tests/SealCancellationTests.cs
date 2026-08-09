using FourSaas.AutoUpdater.Core;
using FourSaas.AutoUpdater.Server;
using FourSaas.AutoUpdater.Storage;
using FourSaas.AutoUpdater.Storage.Memory;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace FourSaas.AutoUpdater.Server.Tests;

public sealed class SealCancellationTests
{
    [Fact]
    public async Task CallerCancellationReleasesClaimAndPreservesStagingForRetry()
    {
        await using var destination = new CancelOnHeadStore();
        await using var staging = new MemoryObjectStore();
        var state = new ManagementState(destination, stagingStore: staging);
        var session = state.OpenSession("repo");
        var sessionId = (string)session.GetType().GetProperty("sessionId")!.GetValue(session)!;
        var payload = "valid staged bytes"u8.ToArray();
        var digest = ContentHash.Compute(payload);
        var request = new DefaultHttpContext();
        request.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
        {
            items = new[] { new { sha256 = digest.ToString(), storedLength = payload.LongLength } }
        }));
        using var grants = await ExecuteJsonAsync(await state.CreateGrantsAsync(sessionId, request.Request, "repo"));
        var stagingKey = new ObjectKey(grants.RootElement.GetProperty("items")[0].GetProperty("stagingKey").GetString()!);
        await staging.PutAsync(stagingKey, new MemoryStream(payload), payload.LongLength);

        using var cancellation = new CancellationTokenSource();
        destination.CancelNextHeadWith(cancellation);
        var cancelled = await state.SealSessionAsync("repo", sessionId, cancellation.Token);

        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsAssignableFrom<IStatusCodeHttpResult>(cancelled).StatusCode);
        Assert.NotNull(await staging.HeadAsync(stagingKey));
        Assert.Empty(await ListAsync(staging, "_trash/staging/"));

        var retried = await state.SealSessionAsync("repo", sessionId);

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(retried).StatusCode);
        var destinationKey = new RepositoryLayout(new RepositoryLayoutTemplates()).Blob(digest);
        Assert.NotNull(await destination.HeadAsync(destinationKey));
    }

    private static async Task<JsonDocument> ExecuteJsonAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(_ => { })
            .BuildServiceProvider();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(context.Response.Body);
    }

    private static async Task<ObjectKey[]> ListAsync(IListableObjectStore store, string prefix)
    {
        var keys = new List<ObjectKey>();
        await foreach (var key in store.ListAsync(prefix)) keys.Add(key);
        return keys.ToArray();
    }

    private sealed class CancelOnHeadStore : IListableObjectStore, IConditionalWriteStore
    {
        private readonly MemoryObjectStore _inner = new();
        private CancellationTokenSource? _cancellation;

        public StorageCapabilities Capabilities => StorageCapabilities.Read | StorageCapabilities.List | StorageCapabilities.Write | StorageCapabilities.ConditionalWrite | StorageCapabilities.Delete;
        public int RecommendedParallelism => _inner.RecommendedParallelism;

        public void CancelNextHeadWith(CancellationTokenSource cancellation) => _cancellation = cancellation;

        public ValueTask<ReadResult?> OpenAsync(ObjectKey key, long offset = 0, ObjectValidator? ifMatch = null, CancellationToken cancellationToken = default)
            => _inner.OpenAsync(key, offset, ifMatch, cancellationToken);

        public ValueTask<ObjectHead?> HeadAsync(ObjectKey key, CancellationToken cancellationToken = default)
        {
            var cancellation = Interlocked.Exchange(ref _cancellation, null);
            if (cancellation is not null)
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return _inner.HeadAsync(key, cancellationToken);
        }

        public IAsyncEnumerable<ObjectKey> ListAsync(string? prefix = null, CancellationToken cancellationToken = default)
            => _inner.ListAsync(prefix, cancellationToken);

        public ValueTask PutAsync(ObjectKey key, Stream content, long? length = null, CancellationToken cancellationToken = default)
            => _inner.PutAsync(key, content, length, cancellationToken);

        public ValueTask DeleteAsync(ObjectKey key, CancellationToken cancellationToken = default)
            => _inner.DeleteAsync(key, cancellationToken);

        public ValueTask<bool> PutIfAbsentAsync(ObjectKey key, Stream content, long? length = null, CancellationToken cancellationToken = default)
            => _inner.PutIfAbsentAsync(key, content, length, cancellationToken);

        public ValueTask<bool> CompareAndSwapAsync(ObjectKey key, ObjectValidator expected, Stream content, long? length = null, CancellationToken cancellationToken = default)
            => _inner.CompareAndSwapAsync(key, expected, content, length, cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
