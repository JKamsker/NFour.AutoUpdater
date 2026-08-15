using System.Net;
using NFour.AutoUpdater.Core;

// Headers that describe a single transport hop rather than the payload. RFC 9110 requires an
// intermediary to strip them; forwarding upstream's Transfer-Encoding or Connection lets the
// upstream framing override Kestrel's own and produces responses the client cannot parse.
var hopByHopHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
    "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Proxy-Connection",
};

var builder = WebApplication.CreateBuilder(args);
var upstream = new Uri(builder.Configuration["Upstream"] ?? "http://localhost:8080/");

builder.Services.AddSingleton(_ => new HttpClient(
    // Without origin pinning the gateway becomes a general-purpose cross-origin fetcher:
    // anything upstream redirects to is fetched with the gateway's own network position and
    // returned to the caller.
    new OriginPinnedHttpHandler([upstream], new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
        AllowAutoRedirect = false,
    }))
{
    Timeout = TimeSpan.FromMinutes(5),
});

var app = builder.Build();

app.MapMethods("/{**path}", ["GET", "HEAD"], async (HttpContext context, HttpClient client, string? path) =>
{
    path ??= string.Empty;
    if (path.StartsWith("_staging/", StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }

    // Build the upstream URI from the escaped path plus the original query string. Composing
    // it from the route path alone silently discarded the query, so any request that carried
    // one was answered with the wrong object.
    if (!Uri.TryCreate(upstream, new Uri(path, UriKind.Relative), out var target)
        || !string.Equals(OriginPinnedHttpHandler.OriginOf(target), OriginPinnedHttpHandler.OriginOf(upstream), StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    var targetUri = new UriBuilder(target) { Query = context.Request.QueryString.HasValue ? context.Request.QueryString.Value!.TrimStart('?') : string.Empty }.Uri;

    using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);
    if (context.Request.Headers.Range.Count > 0) request.Headers.TryAddWithoutValidation("Range", context.Request.Headers.Range.ToString());
    if (context.Request.Headers.IfRange.Count > 0) request.Headers.TryAddWithoutValidation("If-Range", context.Request.Headers.IfRange.ToString());

    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
    if (response.Content.Headers.ContentEncoding.Count != 0)
    {
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        return;
    }
    context.Response.StatusCode = (int)response.StatusCode;
    foreach (var header in response.Headers)
        if (!hopByHopHeaders.Contains(header.Key)) context.Response.Headers[header.Key] = header.Value.ToArray();
    foreach (var header in response.Content.Headers)
        if (!hopByHopHeaders.Contains(header.Key) && !string.Equals(header.Key, "Content-Encoding", StringComparison.OrdinalIgnoreCase))
            context.Response.Headers[header.Key] = header.Value.ToArray();

    if (context.Request.Method != "HEAD") await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
});

app.Run();
