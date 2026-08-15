using NFour.AutoUpdater.Core;
using NFour.AutoUpdater.Storage;
using NFour.AutoUpdater.Storage.Local;
using NFour.AutoUpdater.Storage.Memory;
using System.Text;
using System.Security.Cryptography;
using System.Net;
using System.Net.Http.Headers;

namespace NFour.AutoUpdater.Storage.Tests;

public sealed class StorageTests
{
    private const string UnreachableFtpsServerUri = "ftps://example.invalid/";
    private const string PlaceholderCredential = "test";

    public static IEnumerable<object[]> Stores()
    {
        yield return [new Func<string, IReadableObjectStore>(_ => new MemoryObjectStore())];
        yield return [new Func<string, IReadableObjectStore>(root => new LocalObjectStore(root))];
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ReadWriteRangeAndHeadAreConsistent(Func<string, IReadableObjectStore> factory)
    {
        var root = Path.Combine(Path.GetTempPath(), "4sup-test-" + Guid.NewGuid().ToString("N"));
        await using var store = factory(root);
        var writable = Assert.IsAssignableFrom<IWritableObjectStore>(store);
        var bytes = Encoding.UTF8.GetBytes("0123456789");
        await writable.PutAsync(new ObjectKey("blobs/sha256/00/00/a"), new MemoryStream(bytes));
        var head = await store.HeadAsync(new ObjectKey("blobs/sha256/00/00/a"));
        Assert.NotNull(head); Assert.Equal(bytes.Length, head!.Length);
        var read = await store.OpenAsync(new ObjectKey("blobs/sha256/00/00/a"), 4);
        Assert.NotNull(read);
        await using (read!)
        {
            using var destination = new MemoryStream();
            await read.Content.CopyToAsync(destination);
            Assert.Equal("456789", Encoding.UTF8.GetString(destination.ToArray()));
        }
        if (store is IListableObjectStore list) Assert.Contains("blobs/sha256/00/00/a", await ToArrayAsync(list.ListAsync()));
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ContentAddressedWritesRejectWrongBytesAndRace(Func<string, IReadableObjectStore> factory)
    {
        var root = Path.Combine(Path.GetTempPath(), "4sup-cas-" + Guid.NewGuid().ToString("N"));
        await using var store = factory(root);
        var addressed = Assert.IsAssignableFrom<IContentAddressedWriteStore>(store);
        var bytes = Encoding.UTF8.GetBytes("cas"); var hash = ContentHash.Compute(bytes);
        var key = new ObjectKey("blobs/sha256/ca/s/" + Convert.ToHexString(hash.Span).ToLowerInvariant());
        await Assert.ThrowsAsync<CryptographicException>(async () => await addressed.PutIfAbsentAsync(key, hash, new MemoryStream("bad"u8.ToArray()), 3));
        Assert.True(await addressed.PutIfAbsentAsync(key, hash, new MemoryStream(bytes), bytes.Length));
        Assert.False(await addressed.PutIfAbsentAsync(key, hash, new MemoryStream(bytes), bytes.Length));
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task DelimitedListingReturnsObjectsAndCommonPrefixes(Func<string, IReadableObjectStore> factory)
    {
        var root = Path.Combine(Path.GetTempPath(), "4sup-delimited-" + Guid.NewGuid().ToString("N"));
        await using var store = factory(root);
        var writable = Assert.IsAssignableFrom<IWritableObjectStore>(store);
        await writable.PutAsync(new ObjectKey("packages/a/one"), new MemoryStream("1"u8.ToArray()), 1);
        await writable.PutAsync(new ObjectKey("packages/a/two"), new MemoryStream("2"u8.ToArray()), 1);
        await writable.PutAsync(new ObjectKey("packages/root"), new MemoryStream("3"u8.ToArray()), 1);
        var delimited = Assert.IsAssignableFrom<IDelimitedObjectStore>(store);
        var values = new List<ObjectListing>();
        await foreach (var item in delimited.ListAsync("packages/", "/")) values.Add(item);
        Assert.Contains(values, x => x.CommonPrefix == "packages/a/");
        Assert.Contains(values, x => x.Object?.Value == "packages/root");
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ConditionalCreateIsRaceFree(Func<string, IReadableObjectStore> factory)
    {
        var root = Path.Combine(Path.GetTempPath(), "4sup-race-" + Guid.NewGuid().ToString("N"));
        await using var store = factory(root);
        var conditional = Assert.IsAssignableFrom<IConditionalWriteStore>(store);
        var key = new ObjectKey("race/object");
        var attempts = await Task.WhenAll(Enumerable.Range(0, 16).Select(async index =>
        {
            await using var body = new MemoryStream(Encoding.UTF8.GetBytes($"winner-{index}"), writable: false);
            return await conditional.PutIfAbsentAsync(key, body, body.Length);
        }));

        Assert.Single(attempts, won => won);
        var stored = await store.OpenAsync(key);
        Assert.NotNull(stored);
        await using (stored!)
        {
            using var bytes = new MemoryStream();
            await stored.Content.CopyToAsync(bytes);
            Assert.StartsWith("winner-", Encoding.UTF8.GetString(bytes.ToArray()), StringComparison.Ordinal);
        }
        await conditional.DeleteAsync(new ObjectKey("race/missing"));
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [Fact]
    public async Task DeclaredCapabilitiesMatchInterfaces()
    {
        await using var memory = new MemoryObjectStore();
        Assert.True(StorageCapabilityNegotiation.IsConsistent(memory));
        await using var http = new NFour.AutoUpdater.Storage.Http.HttpObjectStore(new Uri("https://example.invalid/"));
        Assert.True(StorageCapabilityNegotiation.IsConsistent(http));
        await using var ftp = new NFour.AutoUpdater.Storage.Ftp.FtpObjectStore(new Uri(UnreachableFtpsServerUri), new System.Net.NetworkCredential(PlaceholderCredential, PlaceholderCredential));
        Assert.True(StorageCapabilityNegotiation.IsConsistent(ftp));
    }

    [Fact]
    public async Task HttpRejectsContentEncodingAndHeadWithoutLength()
    {
        using var encodedHandler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("bytes"u8.ToArray()) };
            response.Content.Headers.ContentEncoding.Add("gzip");
            response.Content.Headers.ContentLength = 5;
            return response;
        });
        await using (var encoded = new NFour.AutoUpdater.Storage.Http.HttpObjectStore(new Uri("https://example.invalid/"), encodedHandler))
            await Assert.ThrowsAsync<InvalidDataException>(async () => await encoded.HeadAsync(new ObjectKey("blob")));

        using var missingLengthHandler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("bytes"u8.ToArray()) };
            response.Content.Headers.ContentLength = null;
            return response;
        });
        await using var missingLength = new NFour.AutoUpdater.Storage.Http.HttpObjectStore(new Uri("https://example.invalid/"), missingLengthHandler);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await missingLength.HeadAsync(new ObjectKey("blob")));
    }

    [Fact]
    public async Task HttpReportsWhenAnOriginIgnoresRange()
    {
        using var handler = new StubHandler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("0123456789"u8.ToArray()) };
            response.Content.Headers.ContentLength = 10;
            return response;
        });
        await using var store = new NFour.AutoUpdater.Storage.Http.HttpObjectStore(new Uri("https://example.invalid/"), handler);
        var read = await store.OpenAsync(new ObjectKey("blob"), 4);
        Assert.NotNull(read);
        await using (read!)
        {
            Assert.Equal(0, read.ActualStartOffset);
            using var bytes = new MemoryStream();
            await read.Content.CopyToAsync(bytes);
            Assert.Equal("0123456789", Encoding.UTF8.GetString(bytes.ToArray()));
        }
    }

    [Fact]
    public async Task LiveNginxCharacterizationHonorsRawBytesAndRanges()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(HttpIntegrationSettings.GateEnvironmentVariable), HttpIntegrationSettings.EnabledEnvironmentValue, StringComparison.Ordinal))
            Assert.Skip($"Set {HttpIntegrationSettings.GateEnvironmentVariable}={HttpIntegrationSettings.EnabledEnvironmentValue} after starting docker compose to run nginx integration tests.");
        var expectedBytes = HttpIntegrationSettings.ReadExpectedFixtureBytes();
        await using var store = HttpIntegrationSettings.CreateStore();
        var head = await store.HeadAsync(new ObjectKey(HttpIntegrationSettings.FixtureObjectKey));
        Assert.NotNull(head);
        Assert.Equal(expectedBytes.LongLength, head!.Length);
        var read = await store.OpenAsync(new ObjectKey(HttpIntegrationSettings.FixtureObjectKey), HttpIntegrationSettings.RangeStartOffset, head.Validator);
        Assert.NotNull(read);
        await using (read!)
        {
            using var bytes = new MemoryStream();
            await read.Content.CopyToAsync(bytes);
            Assert.Equal(expectedBytes[HttpIntegrationSettings.RangeStartOffset..], bytes.ToArray());
        }
        await using var encoded = HttpIntegrationSettings.CreateStore(new GzipRequestHandler());
        await Assert.ThrowsAsync<InvalidDataException>(async () => await encoded.OpenAsync(new ObjectKey(HttpIntegrationSettings.FixtureObjectKey)));
    }

    private static async Task<string[]> ToArrayAsync(IAsyncEnumerable<ObjectKey> keys) { var result = new List<string>(); await foreach (var key in keys) result.Add(key.Value); return result.ToArray(); }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class GzipRequestHandler : DelegatingHandler
    {
        public GzipRequestHandler() : base(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None }) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            return base.SendAsync(request, cancellationToken);
        }
    }
}
