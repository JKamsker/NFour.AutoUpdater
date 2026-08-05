using System.Net;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(_ => new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None }));
var app = builder.Build();
var upstream = new Uri(app.Configuration["Upstream"] ?? "http://localhost:8080/");
app.MapMethods("/{**path}", ["GET", "HEAD"], async (HttpContext context, HttpClient client, string? path) =>
{
    path ??= string.Empty;
    if (path.StartsWith("_staging/", StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
    var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), new Uri(upstream, path));
    if (context.Request.Headers.Range.Count > 0) request.Headers.TryAddWithoutValidation("Range", context.Request.Headers.Range.ToString());
    if (context.Request.Headers.IfRange.Count > 0) request.Headers.TryAddWithoutValidation("If-Range", context.Request.Headers.IfRange.ToString());
    using var response = await client.SendAsync(request, context.Request.Method == "HEAD" ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
    context.Response.StatusCode = (int)response.StatusCode;
    foreach (var header in response.Headers) context.Response.Headers[header.Key] = header.Value.ToArray();
    foreach (var header in response.Content.Headers) if (!string.Equals(header.Key, "Content-Encoding", StringComparison.OrdinalIgnoreCase)) context.Response.Headers[header.Key] = header.Value.ToArray();
    if (context.Request.Method != "HEAD") await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
});
app.Run();
