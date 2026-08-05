using System.Collections.Concurrent;
using System.Text.Json;
using FourSaas.AutoUpdater.Core;
using FourSaas.AutoUpdater.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ManagementState>();
var app = builder.Build();
app.Use(async (context, next) =>
{
    var isWrite = context.Request.Method is not "GET" and not "HEAD" and not "OPTIONS";
    var configuredToken = Environment.GetEnvironmentVariable("FOURSUP_API_TOKEN");
    if (isWrite && !context.Request.Path.StartsWithSegments("/api/v1/telemetry") && (string.IsNullOrWhiteSpace(configuredToken) || !string.Equals(context.Request.Headers.Authorization.ToString(), "Bearer " + configuredToken, StringComparison.Ordinal)))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "authentication_required" });
        return;
    }
    await next();
});

app.MapGet("/api/v1/.well-known/4sup", (HttpRequest request, ManagementState state) => Results.Json(new { api = $"{request.Scheme}://{request.Host}", versions = new[] { "v1" }, repositoryBaseUrl = state.RepositoryBaseUrl, minimumClientVersion = "1.0.0" }));
app.MapGet("/api/v1/repositories/{repo}/products", (string repo, ManagementState state) => Results.Json(state.Products.Keys.OrderBy(x => x, StringComparer.Ordinal)));
app.MapGet("/api/v1/products/{product}/channels/{channel}", (string product, string channel, ManagementState state) => state.Channels.TryGetValue((product, channel), out var bytes) ? Results.Bytes(bytes, "application/json") : Results.NotFound());
app.MapGet("/api/v1/products/{product}/releases/{release}", (string product, string release, ManagementState state) => state.Releases.TryGetValue((product, release), out var bytes) ? Results.Bytes(bytes, "application/json") : Results.NotFound());
app.MapPost("/api/v1/telemetry", () => Results.Accepted());
app.MapPost("/api/v1/repositories/{repo}/blobs/query", async (HttpRequest request, ManagementState state) =>
{
    var requested = await ReadStringsAsync(request);
    var present = state.QueryBlobs(requested).ToHashSet(StringComparer.Ordinal);
    return Results.Json(new { present = requested.Where(present.Contains).ToArray(), absent = requested.Where(x => !present.Contains(x)).ToArray() });
});
app.MapPost("/api/v1/repositories/{repo}/publish/sessions", (ManagementState state) => Results.Ok(state.OpenSession()));
app.MapPost("/api/v1/publish/sessions/{id}/grants", (string id, HttpRequest request, ManagementState state) => state.CreateGrantsAsync(id, request));
app.MapPost("/api/v1/publish/sessions/{id}/seal", (string id, ManagementState state) => state.SealSession(id));
app.MapPut("/api/v1/products/{product}/releases/{release}/lock", async (string product, string release, HttpRequest request, ManagementState state) => await state.PlaceSignedAsync("release-lock", product, release, request, state.Releases));
app.MapPut("/api/v1/products/{product}/channels/{channel}", async (string product, string channel, HttpRequest request, ManagementState state) => await state.PlaceSignedAsync("channel-pointer", product, channel, request, state.Channels));
app.MapPost("/api/v1/repositories/{repo}/gc", () => Results.Ok(new { status = "queued" }));
app.MapPost("/api/v1/repositories/{repo}/reconcile", () => Results.Ok(new { repaired = Array.Empty<string>(), alerts = Array.Empty<string>() }));
app.Run();

static async ValueTask<string[]> ReadStringsAsync(HttpRequest request)
{
    using var document = await JsonDocument.ParseAsync(request.Body);
    return document.RootElement.TryGetProperty("sha256", out var values) ? values.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray() : [];
}
