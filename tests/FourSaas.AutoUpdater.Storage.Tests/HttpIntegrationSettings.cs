using FourSaas.AutoUpdater.Storage.Http;

namespace FourSaas.AutoUpdater.Storage.Tests;

internal static class HttpIntegrationSettings
{
    public const string GateEnvironmentVariable = "FOURSUP_HTTP";
    public const string EnabledEnvironmentValue = "1";
    public const string ServerUriEnvironmentVariable = "FOURSUP_HTTP_URI";
    public const string FixtureObjectKey = "blob";
    public const int RangeStartOffset = 4;

    public static HttpObjectStore CreateStore(HttpMessageHandler? handler = null)
    {
        var serverUri = new Uri(GetRequiredEnvironmentVariable(ServerUriEnvironmentVariable));
        return handler is null ? new HttpObjectStore(serverUri) : new HttpObjectStore(serverUri, handler);
    }

    public static byte[] ReadExpectedFixtureBytes()
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "http", "blob"));

    private static string GetRequiredEnvironmentVariable(string name)
        => Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"The {name} environment variable is required for HTTP integration tests.");
}
