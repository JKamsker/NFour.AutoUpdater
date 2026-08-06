using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FourSaas.AutoUpdater.Core;
using FourSaas.AutoUpdater.Server;
using FourSaas.AutoUpdater.Storage;
using FourSaas.AutoUpdater.Storage.Local;
using FourSaas.AutoUpdater.Storage.S3;
using FourSaas.AutoUpdater.Storage.Ftp;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var storageRoot = Environment.GetEnvironmentVariable("FOURSUP_STORAGE_ROOT");
var storageBackend = Environment.GetEnvironmentVariable("FOURSUP_STORAGE_BACKEND") ?? "local";
if (storageBackend == "s3")
{
    var bucket = Environment.GetEnvironmentVariable("FOURSUP_STORAGE_BUCKET") ?? throw new InvalidOperationException("FOURSUP_STORAGE_BUCKET is required for the S3 server backend.");
    builder.Services.AddSingleton<IWritableObjectStore>(_ => CreateS3Store(bucket, Environment.GetEnvironmentVariable("FOURSUP_STORAGE_PREFIX") ?? ""));
}
else if (!string.IsNullOrWhiteSpace(storageRoot)) builder.Services.AddSingleton<IWritableObjectStore>(_ => new LocalObjectStore(storageRoot));
else if (storageBackend == "ftp")
{
    var ftpText = Environment.GetEnvironmentVariable("FOURSUP_STORAGE_FTP_URI") ?? throw new InvalidOperationException("FOURSUP_STORAGE_FTP_URI is required for the FTP server backend.");
    if (!Uri.TryCreate(ftpText, UriKind.Absolute, out var ftpUri) || ftpUri.Scheme is not ("ftp" or "ftps")) throw new InvalidOperationException("FOURSUP_STORAGE_FTP_URI must be an absolute ftp(s) URI.");
    var ftpUser = Environment.GetEnvironmentVariable("FOURSUP_STORAGE_FTP_USER") ?? ftpUri.UserInfo.Split(':').FirstOrDefault() ?? "anonymous";
    var ftpPassword = Environment.GetEnvironmentVariable("FOURSUP_STORAGE_FTP_PASSWORD") ?? "anonymous@";
    builder.Services.AddSingleton<IWritableObjectStore>(_ => new FtpObjectStore(ftpUri, new NetworkCredential(ftpUser, ftpPassword), enableSsl: ftpUri.Scheme == "ftps"));
}
var databaseConnection = Environment.GetEnvironmentVariable("FOURSUP_DATABASE");
if (!string.IsNullOrWhiteSpace(storageRoot) || storageBackend is "s3" or "ftp")
    if (string.IsNullOrWhiteSpace(databaseConnection) && !string.Equals(Environment.GetEnvironmentVariable("FOURSUP_ALLOW_EPHEMERAL_STATE"), "1", StringComparison.Ordinal))
        throw new InvalidOperationException("FOURSUP_DATABASE is required for a served control plane; set FOURSUP_ALLOW_EPHEMERAL_STATE=1 only for single-process development.");
if (!string.IsNullOrWhiteSpace(databaseConnection))
{
    builder.Services.AddDbContextFactory<ManagementDbContext>(options => options.UseNpgsql(databaseConnection));
    builder.Services.AddHostedService<TelemetryRetentionService>();
}
var oidcAuthority = Environment.GetEnvironmentVariable("FOURSUP_OIDC_AUTHORITY");
if (!string.IsNullOrWhiteSpace(oidcAuthority))
{
    var oidcAudience = Environment.GetEnvironmentVariable("FOURSUP_OIDC_AUDIENCE");
    if (string.IsNullOrWhiteSpace(oidcAudience)) throw new InvalidOperationException("FOURSUP_OIDC_AUDIENCE is required when OIDC authentication is enabled.");
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
    {
        options.Authority = oidcAuthority;
        options.Audience = oidcAudience;
        options.RequireHttpsMetadata = !string.Equals(Environment.GetEnvironmentVariable("FOURSUP_OIDC_ALLOW_HTTP"), "1", StringComparison.Ordinal);
    });
    builder.Services.AddAuthorization();
}
builder.Services.AddSingleton<ManagementState>(serviceProvider =>
{
    var servedStore = serviceProvider.GetService<IWritableObjectStore>();
    var repositoryId = Environment.GetEnvironmentVariable("FOURSUP_REPOSITORY_ID");
    if (servedStore is not null && string.IsNullOrWhiteSpace(repositoryId)) throw new InvalidOperationException("FOURSUP_REPOSITORY_ID must be configured for a served repository.");
    var stagingRoot = Environment.GetEnvironmentVariable("FOURSUP_STAGING_ROOT");
    var stagingBackend = Environment.GetEnvironmentVariable("FOURSUP_STAGING_BACKEND") ?? "local";
    if (servedStore is not null && stagingBackend == "local" && string.IsNullOrWhiteSpace(stagingRoot)) throw new InvalidOperationException("FOURSUP_STAGING_ROOT must be configured separately from FOURSUP_STORAGE_ROOT.");
    if (servedStore is not null && stagingBackend == "local" && !string.IsNullOrWhiteSpace(storageRoot) && !string.IsNullOrWhiteSpace(stagingRoot))
    {
        var servedPath = Path.GetFullPath(storageRoot);
        var stagingPath = Path.GetFullPath(stagingRoot);
        var servedPrefix = servedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var stagingPrefix = stagingPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (string.Equals(servedPath, stagingPath, StringComparison.OrdinalIgnoreCase) || stagingPath.StartsWith(servedPrefix, StringComparison.OrdinalIgnoreCase) || servedPath.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("FOURSUP_STAGING_ROOT must be a separate root from FOURSUP_STORAGE_ROOT.");
    }
    IWritableObjectStore? stagingStore = null;
    if (servedStore is not null && stagingBackend == "s3")
    {
        var stagingBucket = Environment.GetEnvironmentVariable("FOURSUP_STAGING_BUCKET") ?? throw new InvalidOperationException("FOURSUP_STAGING_BUCKET is required for S3 staging.");
        var stagingPrefix = Environment.GetEnvironmentVariable("FOURSUP_STAGING_PREFIX") ?? "";
        var servedPrefix = Environment.GetEnvironmentVariable("FOURSUP_STORAGE_PREFIX") ?? "";
        if (string.Equals(stagingBucket, Environment.GetEnvironmentVariable("FOURSUP_STORAGE_BUCKET"), StringComparison.Ordinal) && (string.IsNullOrWhiteSpace(stagingPrefix) || string.IsNullOrWhiteSpace(servedPrefix) || stagingPrefix.StartsWith(servedPrefix, StringComparison.Ordinal) || servedPrefix.StartsWith(stagingPrefix, StringComparison.Ordinal)))
            throw new InvalidOperationException("FOURSUP_STAGING_PREFIX must be a non-overlapping prefix when S3 staging shares the served bucket.");
        stagingStore = CreateS3Store(stagingBucket, stagingPrefix);
    }
    else if (servedStore is not null && stagingBackend == "local") stagingStore = new LocalObjectStore(stagingRoot!);
    if (servedStore is not null && stagingBackend == "ftp")
    {
        var stagingFtpText = Environment.GetEnvironmentVariable("FOURSUP_STAGING_FTP_URI") ?? throw new InvalidOperationException("FOURSUP_STAGING_FTP_URI is required for FTP staging.");
        if (!Uri.TryCreate(stagingFtpText, UriKind.Absolute, out var stagingFtpUri) || stagingFtpUri.Scheme is not ("ftp" or "ftps")) throw new InvalidOperationException("FOURSUP_STAGING_FTP_URI must be an absolute ftp(s) URI.");
        var stagingUser = Environment.GetEnvironmentVariable("FOURSUP_STAGING_FTP_USER") ?? stagingFtpUri.UserInfo.Split(':').FirstOrDefault() ?? "anonymous";
        var stagingPassword = Environment.GetEnvironmentVariable("FOURSUP_STAGING_FTP_PASSWORD") ?? "anonymous@";
        stagingStore = new FtpObjectStore(stagingFtpUri, new NetworkCredential(stagingUser, stagingPassword), enableSsl: stagingFtpUri.Scheme == "ftps");
    }
    else if (servedStore is not null && stagingBackend is not ("local" or "s3"))
        throw new InvalidOperationException($"Unsupported FOURSUP_STAGING_BACKEND '{stagingBackend}'.");
    var persistencePath = string.IsNullOrWhiteSpace(databaseConnection) ? Environment.GetEnvironmentVariable("FOURSUP_STATE_PATH") ?? Path.Combine(Directory.GetCurrentDirectory(), "4sup-state.json") : null;
    var state = new ManagementState(servedStore, persistencePath, stagingStore, serviceProvider.GetService<IDbContextFactory<ManagementDbContext>>(), repositoryId)
    {
        RepositoryBaseUrl = Environment.GetEnvironmentVariable("FOURSUP_REPOSITORY_BASE_URL") ?? ""
    };
    var configured = Environment.GetEnvironmentVariable("FOURSUP_TRUSTED_KEYS");
    if (!string.IsNullOrWhiteSpace(configured))
    {
        var keys = configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(value =>
        {
            var separator = value.IndexOf(':');
            if (separator <= 0) throw new FormatException("FOURSUP_TRUSTED_KEYS entries must use keyId:base64url.");
            return (value[..separator], Base64Url.Decode(value[(separator + 1)..]));
        });
        state.ConfigureTrustedKeys(keys);
    }
    return state;
});
var app = builder.Build();
var telemetryWindows = new ConcurrentDictionary<string, (DateTimeOffset Window, int Count)>(StringComparer.Ordinal);
app.Use(async (context, next) =>
{
    if (context.Request.Method == "POST" && context.Request.Path.StartsWithSegments("/api/v1/telemetry"))
    {
        var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = DateTimeOffset.UtcNow;
        var current = telemetryWindows.AddOrUpdate(key, (now, 1), (_, previous) => now - previous.Window >= TimeSpan.FromMinutes(1) ? (now, 1) : (previous.Window, previous.Count + 1));
        if (current.Count > 30)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }
    }
    await next();
});
var managementState = app.Services.GetRequiredService<ManagementState>();
if (!string.IsNullOrWhiteSpace(databaseConnection))
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<ManagementDbContext>();
    await database.Database.MigrateAsync();
    await managementState.LoadDatabaseAsync();
}
await managementState.EnsureRepositoryDescriptorAsync();
if (!string.IsNullOrWhiteSpace(oidcAuthority))
{
    app.UseAuthentication();
    app.UseAuthorization();
}
app.Use(async (context, next) =>
{
    var isWrite = context.Request.Method is not "GET" and not "HEAD" and not "OPTIONS";
    var publisherToken = Environment.GetEnvironmentVariable("FOURSUP_PUBLISHER_TOKEN");
    var operatorToken = Environment.GetEnvironmentVariable("FOURSUP_OPERATOR_TOKEN");
    var compatibilityToken = Environment.GetEnvironmentVariable("FOURSUP_API_TOKEN");
    var compatibilityRole = Environment.GetEnvironmentVariable("FOURSUP_API_TOKEN_ROLE");
    var staticExpiry = DateTimeOffset.TryParse(Environment.GetEnvironmentVariable("FOURSUP_STATIC_TOKEN_EXPIRES_AT"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedExpiry) ? parsedExpiry : (DateTimeOffset?)null;
    var staticTokensAllowed = string.IsNullOrWhiteSpace(oidcAuthority) && string.Equals(Environment.GetEnvironmentVariable("FOURSUP_ALLOW_STATIC_TOKENS"), "1", StringComparison.Ordinal) && staticExpiry is { } expiry && expiry > DateTimeOffset.UtcNow;
    var supplied = context.Request.Headers.Authorization.ToString();
    var oidcRole = context.User.Identity?.IsAuthenticated == true ? context.User.Claims.Where(x => x.Type is "role" or "roles" or "scope").SelectMany(x => x.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).FirstOrDefault(x => x is "operator" or "publisher") : null;
    var role = oidcRole ?? (staticTokensAllowed && string.Equals(supplied, "Bearer " + operatorToken, StringComparison.Ordinal) ? "operator" : staticTokensAllowed && string.Equals(supplied, "Bearer " + publisherToken, StringComparison.Ordinal) ? "publisher" : staticTokensAllowed && string.Equals(compatibilityRole, "operator", StringComparison.Ordinal) && string.Equals(supplied, "Bearer " + compatibilityToken, StringComparison.Ordinal) ? "operator" : staticTokensAllowed && string.Equals(compatibilityRole, "publisher", StringComparison.Ordinal) && string.Equals(supplied, "Bearer " + compatibilityToken, StringComparison.Ordinal) ? "publisher" : null);
    context.Items["4sup-role"] = role;
    var endpointPolicy = context.GetEndpoint()?.Metadata.GetMetadata<EndpointPolicyMetadata>()?.Policy;
    var requiredRole = endpointPolicy is "anonymous" or null ? RequiredRole(context.Request) : endpointPolicy;
    if (isWrite && requiredRole is not null && role is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "authentication_required" });
        return;
    }
    if (requiredRole is not null && !string.Equals(role, requiredRole, StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "insufficient_role", required = requiredRole });
        return;
    }
    if (string.Equals(requiredRole, "operator", StringComparison.Ordinal) && isWrite && string.Equals(Environment.GetEnvironmentVariable("FOURSUP_REQUIRE_OPERATOR_MFA"), "1", StringComparison.Ordinal))
    {
        var hasMfa = context.User.Claims.Where(x => x.Type is "amr" or "acr" or "mfa").Any(x => x.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("mfa", StringComparer.OrdinalIgnoreCase) || string.Equals(x.Value, "true", StringComparison.OrdinalIgnoreCase));
        var stepUp = context.Request.Headers.TryGetValue("X-4sup-step-up", out var suppliedStepUp) && string.Equals(suppliedStepUp.ToString(), Environment.GetEnvironmentVariable("FOURSUP_OPERATOR_STEP_UP"), StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FOURSUP_OPERATOR_STEP_UP"));
        if (!hasMfa && !stepUp)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "step_up_required" });
            return;
        }
    }
    await next();
});

var discovery = (HttpRequest request, ManagementState state) => Results.Json(new { api = $"{request.Scheme}://{request.Host}", versions = new[] { "v1" }, repositoryBaseUrl = state.RepositoryBaseUrl, minimumClientVersion = "1.0.0" });
app.MapGet("/.well-known/4sup", discovery).WithMetadata(new EndpointPolicyMetadata("anonymous"));
// Keep the versioned alias for older control-plane clients while the normative
// discovery route remains at the root of the server host.
app.MapGet("/api/v1/.well-known/4sup", discovery).WithMetadata(new EndpointPolicyMetadata("anonymous"));
app.MapGet("/health/ready", (ManagementState state) => state.IsRepositoryUnavailable
    ? Results.Json(new { ready = false, unavailable = true }, statusCode: StatusCodes.Status503ServiceUnavailable)
    : Results.Ok(new { ready = true, unavailable = false })).WithMetadata(new EndpointPolicyMetadata("anonymous"));
app.MapGet("/api/v1/repositories/{repo}/products", (string repo, ManagementState state) => state.IsRepositoryUnavailable ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable) : state.IsConfiguredRepository(repo) ? Results.Json(state.Products.Keys.OrderBy(x => x, StringComparer.Ordinal)) : Results.NotFound()).WithMetadata(new EndpointPolicyMetadata("anonymous"));
app.MapGet("/api/v1/products/{product}/channels/{channel}", (string product, string channel, ManagementState state, HttpContext context) => state.ReadPublicDocumentAsync(new RepositoryLayout(new RepositoryLayoutTemplates()).Channel(product, channel), state.Channels, (product, channel), context.RequestAborted)).WithMetadata(new EndpointPolicyMetadata("anonymous"));
app.MapGet("/api/v1/products/{product}/releases/{release}", (string product, string release, ManagementState state, HttpContext context) => state.ReadPublicDocumentAsync(new RepositoryLayout(new RepositoryLayoutTemplates()).Release(product, release), state.Releases, (product, release), context.RequestAborted)).WithMetadata(new EndpointPolicyMetadata("anonymous"));
app.MapGet("/api/v1/products/{product}/revocations", (string product, ManagementState state, HttpContext context) => state.ReadPublicDocumentAsync(new RepositoryLayout(new RepositoryLayoutTemplates()).Revocations(product), state.Revocations, (product, "revocations"), context.RequestAborted)).WithMetadata(new EndpointPolicyMetadata("anonymous"));
app.MapPost("/api/v1/telemetry", (HttpRequest request, ManagementState state) => state.RecordTelemetryAsync(request, request.HttpContext.RequestAborted)).WithMetadata(new EndpointPolicyMetadata("anonymous"));
app.MapPost("/api/v1/repositories/{repo}/blobs/query", async (string repo, HttpRequest request, ManagementState state) =>
{
    var requested = await ReadStringsAsync(request);
    var present = (await state.QueryBlobsAsync(repo, requested, request.HttpContext.RequestAborted).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
    return Results.Json(new { present = requested.Where(present.Contains).ToArray(), absent = requested.Where(x => !present.Contains(x)).ToArray() });
}).WithMetadata(new EndpointPolicyMetadata("publisher"));
app.MapPost("/api/v1/repositories/{repo}/sequences/{scope}/{name}", (string repo, string scope, string name, ManagementState state, HttpContext context) => state.AllocateSequenceAsync(repo, scope, name, context.RequestAborted)).WithMetadata(new EndpointPolicyMetadata("publisher"));
app.MapPost("/api/v1/repositories/{repo}/packages/{package}/versions", (string repo, string package, HttpRequest request, ManagementState state) => state.RegisterPackageVersionAsync(repo, package, request, request.HttpContext.RequestAborted)).WithMetadata(new EndpointPolicyMetadata("publisher"));
app.MapPost("/api/v1/repositories/{repo}/packages/{package}/versions/{version}/files", (string repo, string package, string version, HttpRequest request, ManagementState state) => state.RegisterFileTableAsync(repo, package, version, request, request.HttpContext.RequestAborted)).WithMetadata(new EndpointPolicyMetadata("publisher"));
app.MapPost("/api/v1/repositories/{repo}/packages/{package}/versions/{version}/publish", (string repo, string package, string version, ManagementState state) => state.PublishPackageVersion(repo, package, version)).WithMetadata(new EndpointPolicyMetadata("publisher"));
app.MapPost("/api/v1/repositories/{repo}/publish/sessions", (string repo, ManagementState state) => Results.Ok(state.OpenSession(repo))).WithMetadata(new EndpointPolicyMetadata("publisher"));
app.MapPost("/api/v1/repositories/{repo}/publish/sessions/{id}/grants", (string repo, string id, HttpRequest request, ManagementState state) => state.CreateGrantsAsync(id, request, repo)).WithMetadata(new EndpointPolicyMetadata("publisher"));
app.MapPost("/api/v1/repositories/{repo}/publish/sessions/{sessionId}/grants/{grantId}/complete", (string repo, string sessionId, string grantId, HttpRequest request, ManagementState state) => state.CompleteMultipartGrantAsync(repo, sessionId, grantId, request, request.HttpContext.RequestAborted)).WithMetadata(new EndpointPolicyMetadata("publisher"));
app.MapGet("/api/v1/repositories/{repo}/publish/sessions/{sessionId}/grants/{grantId}/parts", (string repo, string sessionId, string grantId, ManagementState state, HttpContext context) => state.ListMultipartGrantPartsAsync(repo, sessionId, grantId, context.RequestAborted)).WithMetadata(new EndpointPolicyMetadata("publisher"));
app.MapPost("/api/v1/repositories/{repo}/publish/sessions/{sessionId}/grants/{grantId}/abort", (string repo, string sessionId, string grantId, ManagementState state, HttpContext context) => state.AbortMultipartGrantAsync(repo, sessionId, grantId, context.RequestAborted)).WithMetadata(new EndpointPolicyMetadata("publisher"));
app.MapPost("/api/v1/repositories/{repo}/publish/sessions/{id}/seal", (string repo, string id, ManagementState state, HttpContext context) => state.SealSessionAsync(repo, id, context.RequestAborted)).WithMetadata(new EndpointPolicyMetadata("publisher"));
app.MapPut("/api/v1/products/{product}/releases/{release}/lock", async (string product, string release, HttpRequest request, ManagementState state) => await state.PlaceSignedAsync("release-lock", product, release, request, state.Releases)).WithMetadata(new EndpointPolicyMetadata("operator"));
app.MapPut("/api/v1/products/{product}/channels/{channel}", async (string product, string channel, HttpRequest request, ManagementState state) => await state.PlaceSignedAsync("channel-pointer", product, channel, request, state.Channels)).WithMetadata(new EndpointPolicyMetadata("operator"));
app.MapPut("/api/v1/repositories/{repo}/keys", (string repo, HttpRequest request, ManagementState state) => state.PlaceSignedKeyManifestAsync(repo, request, request.HttpContext.RequestAborted)).WithMetadata(new EndpointPolicyMetadata("operator"));
app.MapPost("/api/v1/products/{product}/releases/drafts", (string product, HttpRequest request, ManagementState state) => state.CreateReleaseDraftAsync(product, request, request.HttpContext.RequestAborted)).WithMetadata(new EndpointPolicyMetadata("publisher"));
app.MapPost("/api/v1/products/{product}/releases/drafts/{draft}/check", (string product, string draft, ManagementState state) => state.CheckReleaseDraft(product, draft)).WithMetadata(new EndpointPolicyMetadata("publisher"));
app.MapPost("/api/v1/products/{product}/releases/drafts/{draft}/coverage", (string product, string draft, HttpRequest request, ManagementState state) => state.RegisterReleaseDraftCoverageAsync(product, draft, request, request.HttpContext.RequestAborted)).WithMetadata(new EndpointPolicyMetadata("publisher"));
app.MapPost("/api/v1/products/{product}/releases/{release}/yank", async (string product, string release, HttpRequest request, ManagementState state) => await state.PlaceSignedAsync("revocation", product, release, request, state.Revocations)).WithMetadata(new EndpointPolicyMetadata("operator"));
app.MapPost("/api/v1/blobs/{hash}/quarantine", (string hash, ManagementState state, HttpContext context) => state.QuarantineBlobAsync(hash, context.RequestAborted)).WithMetadata(new EndpointPolicyMetadata("operator"));
app.MapPost("/api/v1/repositories/{repo}/gc", async (string repo, HttpRequest request, ManagementState state) =>
{
    var dryRun = request.Query.TryGetValue("dryRun", out var value) && bool.TryParse(value, out var parsed) && parsed;
    return await state.CollectGcAsync(repo, dryRun, request.HttpContext.RequestAborted);
}).WithMetadata(new EndpointPolicyMetadata("operator"));
app.MapPost("/api/v1/repositories/{repo}/publish/gc", async (string repo, HttpRequest request, ManagementState state) =>
{
    var dryRun = request.Query.TryGetValue("dryRun", out var value) && bool.TryParse(value, out var parsed) && parsed;
    return await state.CollectStagingGcAsync(repo, dryRun, request.HttpContext.RequestAborted);
}).WithMetadata(new EndpointPolicyMetadata("operator"));
app.MapPost("/api/v1/repositories/{repo}/reconcile", (string repo, ManagementState state, HttpContext context) => state.ReconcileAsync(repo, context.RequestAborted)).WithMetadata(new EndpointPolicyMetadata("operator"));
var unclassifiedEndpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
    .Where(x => x.DisplayName?.Contains("/api/v1/", StringComparison.Ordinal) == true && x.Metadata.GetMetadata<EndpointPolicyMetadata>() is null)
    .Select(x => x.DisplayName)
    .ToArray();
if (unclassifiedEndpoints.Length != 0) throw new InvalidOperationException($"Every control-plane endpoint must declare an explicit policy: {string.Join(", ", unclassifiedEndpoints)}");
app.Run();

static S3ProviderProfile S3ProviderProfileFromEnvironment()
{
    var value = Environment.GetEnvironmentVariable("FOURSUP_S3_PROVIDER") ?? "aws";
    if (!Enum.TryParse<S3ProviderProfile>(value, true, out var profile)) throw new InvalidOperationException($"Unknown FOURSUP_S3_PROVIDER '{value}'.");
    return profile;
}

static S3ObjectStore CreateS3Store(string bucket, string prefix)
{
    var profile = S3ProviderProfileFromEnvironment();
    var serviceUrl = Uri.TryCreate(Environment.GetEnvironmentVariable("FOURSUP_S3_ENDPOINT"), UriKind.Absolute, out var endpoint) ? endpoint : null;
    var accessKey = Environment.GetEnvironmentVariable("FOURSUP_S3_ACCESS_KEY");
    var secretKey = Environment.GetEnvironmentVariable("FOURSUP_S3_SECRET_KEY");
    return profile is S3ProviderProfile.Aws or S3ProviderProfile.Minio or S3ProviderProfile.R2
        ? new S3ConditionalObjectStore(bucket, prefix, serviceUrl: serviceUrl, accessKey: accessKey, secretKey: secretKey, providerProfile: profile)
        : new S3ObjectStore(bucket, prefix, serviceUrl: serviceUrl, accessKey: accessKey, secretKey: secretKey, providerProfile: profile);
}

static async ValueTask<string[]> ReadStringsAsync(HttpRequest request)
{
    // Bounded like every other management request body. Parsing an unbounded stream lets a
    // single request allocate until the process dies, regardless of what validation would
    // have rejected afterwards.
    const int MaximumBytes = 4 * 1024 * 1024;
    using var bounded = new MemoryStream();
    var buffer = new byte[64 * 1024];
    long total = 0;
    while (true)
    {
        var read = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted);
        if (read == 0) break;
        total += read;
        if (total > MaximumBytes) throw new InvalidDataException($"Request body exceeds the {MaximumBytes} byte limit.");
        await bounded.WriteAsync(buffer.AsMemory(0, read), request.HttpContext.RequestAborted);
    }
    bounded.Position = 0;
    using var document = await JsonDocument.ParseAsync(bounded);
    return document.RootElement.TryGetProperty("sha256", out var values) ? values.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray() : [];
}

static string? RequiredRole(HttpRequest request)
{
    if (request.Method is "GET" or "HEAD" or "OPTIONS" || request.Path.StartsWithSegments("/api/v1/telemetry")) return null;
    if (request.Path.StartsWithSegments("/api/v1/repositories") && request.Path.Value?.Contains("/publish/gc", StringComparison.Ordinal) == true) return "operator";
    if (request.Path.StartsWithSegments("/api/v1/repositories") && request.Path.Value?.Contains("/sequences/", StringComparison.Ordinal) == true) return "publisher";
    if (request.Path.StartsWithSegments("/api/v1/repositories") && (request.Path.Value?.Contains("/blobs/query", StringComparison.Ordinal) == true || request.Path.Value?.Contains("/publish/", StringComparison.Ordinal) == true || request.Path.Value?.Contains("/packages/", StringComparison.Ordinal) == true)) return "publisher";
    if (request.Path.Value?.Contains("/releases/drafts", StringComparison.Ordinal) == true) return "publisher";
    if (request.Path.Value?.Contains("/channels/", StringComparison.Ordinal) == true || request.Path.Value?.Contains("/releases/", StringComparison.Ordinal) == true || request.Path.Value?.EndsWith("/gc", StringComparison.Ordinal) == true || request.Path.Value?.EndsWith("/reconcile", StringComparison.Ordinal) == true) return "operator";
    return "operator";
}
