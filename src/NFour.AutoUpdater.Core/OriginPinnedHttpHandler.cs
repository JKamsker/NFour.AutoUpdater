using System.Net;
using System.Net.Http;

namespace NFour.AutoUpdater.Core;

/// <summary>
/// Follows HTTP redirects manually, validating every hop against an explicit origin
/// allowlist.
///
/// <see cref="HttpClientHandler.AllowAutoRedirect"/> validates only the URI the caller
/// supplied; the redirect chain that follows is unconstrained.  A compromised origin, proxy
/// or DNS path can therefore point a repository read at an internal address (request
/// forgery), downgrade the transport to cleartext, or send an upload's bytes and its
/// storage-required headers somewhere else entirely.  Redirects are useful enough to keep —
/// object stores use them for presigned URLs — so they are followed here, but only to an
/// origin the caller named up front.
/// </summary>
public sealed class OriginPinnedHttpHandler : DelegatingHandler
{
    private const int DefaultMaximumHops = 5;

    private readonly IReadOnlySet<string> _allowedOrigins;
    private readonly int _maximumHops;
    private readonly bool _requireHttps;

    /// <param name="allowedOrigins">
    /// Origins a redirect may target. Compared as scheme+host+port, so a redirect to a
    /// different host, a different port, or a downgraded scheme is refused.
    /// </param>
    /// <param name="requireHttps">
    /// When true, every hop must be HTTPS. Defaults to true when the first allowed origin is
    /// HTTPS, so an HTTPS deployment cannot be silently downgraded.
    /// </param>
    public OriginPinnedHttpHandler(IEnumerable<Uri> allowedOrigins, HttpMessageHandler? innerHandler = null, int maximumHops = DefaultMaximumHops, bool? requireHttps = null)
    {
        var origins = allowedOrigins.Select(OriginOf).ToHashSet(StringComparer.Ordinal);
        if (origins.Count == 0) throw new ArgumentException("At least one allowed origin is required.", nameof(allowedOrigins));
        _allowedOrigins = origins;
        _maximumHops = maximumHops > 0 ? maximumHops : throw new ArgumentOutOfRangeException(nameof(maximumHops));
        _requireHttps = requireHttps ?? origins.All(x => x.StartsWith("https://", StringComparison.Ordinal));
        InnerHandler = innerHandler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None
        };
    }

    /// <summary>Builds an <see cref="HttpClient"/> pinned to the given origins.</summary>
    public static HttpClient CreateClient(IEnumerable<Uri> allowedOrigins, HttpMessageHandler? innerHandler = null)
        => new(new OriginPinnedHttpHandler(allowedOrigins, innerHandler));

    /// <summary>Normalised scheme://host:port for <paramref name="uri"/>.</summary>
    public static string OriginOf(Uri uri)
        => $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}:{(uri.IsDefaultPort ? DefaultPort(uri.Scheme) : uri.Port)}";

    private static int DefaultPort(string scheme)
        => string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        EnsureAllowed(request.RequestUri, "request");

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        HttpRequestMessage? owned = null;
        try
        {
        for (var hop = 0; IsRedirect(response.StatusCode); hop++)
        {
            if (hop >= _maximumHops)
            {
                response.Dispose();
                throw new HttpRequestException($"Redirect chain exceeded {_maximumHops} hops.");
            }

            var location = response.Headers.Location;
            if (location is null)
            {
                response.Dispose();
                throw new HttpRequestException($"Received {(int)response.StatusCode} with no Location header.");
            }

            var target = location.IsAbsoluteUri ? location : new Uri(request.RequestUri!, location);

            // A request carrying a body cannot be safely replayed: the content stream is
            // usually forward-only, and 307/308 require re-sending it verbatim. Refusing is
            // better than silently sending a truncated or empty body to a second origin.
            if (request.Content is not null)
            {
                response.Dispose();
                throw new HttpRequestException($"Refusing to follow a {(int)response.StatusCode} redirect for a request with a body ('{request.Method}' to '{Redact(target)}').");
            }

            try { EnsureAllowed(target, "redirect target"); }
            catch { response.Dispose(); throw; }

            var status = response.StatusCode;
            response.Dispose();

            // Only clones this handler created are disposed here; the first request belongs
            // to the caller.
            var next = CloneWithoutContent(request, target, status);
            owned?.Dispose();
            owned = next;
            request = next;
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        return response;
        }
        finally { owned?.Dispose(); }
    }

    private void EnsureAllowed(Uri? uri, string what)
    {
        if (uri is null || !uri.IsAbsoluteUri) throw new HttpRequestException($"An absolute {what} URI is required.");
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw new HttpRequestException($"Refusing a {what} URI containing userinfo.");
        if (_requireHttps && !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException($"Refusing a non-HTTPS {what} URI '{Redact(uri)}'; transport downgrade is not permitted.");
        if (!_allowedOrigins.Contains(OriginOf(uri)))
            throw new HttpRequestException($"Refusing a {what} to unpinned origin '{OriginOf(uri)}'.");
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static HttpRequestMessage CloneWithoutContent(HttpRequestMessage source, Uri target, HttpStatusCode status)
    {
        // 303, and by long-standing practice 301/302, turn the follow-up into a GET.
        var method = status is HttpStatusCode.SeeOther or HttpStatusCode.MovedPermanently or HttpStatusCode.Found
            ? (source.Method == HttpMethod.Head ? HttpMethod.Head : HttpMethod.Get)
            : source.Method;
        var clone = new HttpRequestMessage(method, target) { Version = source.Version, VersionPolicy = source.VersionPolicy };
        foreach (var header in source.Headers)
        {
            // Credentials are scoped to the origin that issued them and are not carried
            // across a hop, even a permitted one.
            if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(header.Key, "Proxy-Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }

    private static string Redact(Uri uri) => $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath}";
}
