using FourSaas.AutoUpdater.Core;
using System.Net;

namespace FourSaas.AutoUpdater.Core.Tests;

public sealed class OriginPinnedHttpHandlerTests
{
    [Fact]
    public async Task CrossOriginRedirectIsRejectedBeforeSecondRequest()
    {
        using var transport = new RedirectTransport(new Uri("https://other.example/blob"));
        using var client = OriginPinnedHttpHandler.CreateClient([new Uri("https://repo.example/")], transport);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("https://repo.example/blob"));

        Assert.Contains("unpinned origin", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, transport.RequestCount);
    }

    [Fact]
    public async Task HttpsToHttpRedirectIsRejectedAsDowngrade()
    {
        using var transport = new RedirectTransport(new Uri("http://repo.example/blob"));
        using var client = OriginPinnedHttpHandler.CreateClient([new Uri("https://repo.example/")], transport);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("https://repo.example/blob"));

        Assert.Contains("non-HTTPS", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, transport.RequestCount);
    }

    [Fact]
    public async Task RedirectLoopStopsAtConfiguredHopLimit()
    {
        using var transport = new RedirectTransport(new Uri("https://repo.example/blob"));
        using var client = new HttpClient(new OriginPinnedHttpHandler(
            [new Uri("https://repo.example/")], transport, maximumHops: 2));

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("https://repo.example/blob"));

        Assert.Contains("exceeded 2 hops", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, transport.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task BodyIsNeverReplayedAcross307Or308(HttpStatusCode status)
    {
        using var transport = new RedirectTransport(new Uri("https://repo.example/upload"), status);
        using var client = OriginPinnedHttpHandler.CreateClient([new Uri("https://repo.example/")], transport);
        using var request = new HttpRequestMessage(HttpMethod.Put, "https://repo.example/upload")
        {
            Content = new ByteArrayContent("publisher bytes"u8.ToArray())
        };

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));

        Assert.Contains("request with a body", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, transport.RequestCount);
    }

    private sealed class RedirectTransport(Uri target, HttpStatusCode status = HttpStatusCode.Found) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var response = new HttpResponseMessage(status);
            response.Headers.Location = target;
            return Task.FromResult(response);
        }
    }
}
