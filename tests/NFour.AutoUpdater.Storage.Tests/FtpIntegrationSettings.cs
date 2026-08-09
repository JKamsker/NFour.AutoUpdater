using NFour.AutoUpdater.Storage.Ftp;
using System.Net;

namespace NFour.AutoUpdater.Storage.Tests;

internal static class FtpIntegrationSettings
{
    private const int ExclusiveLeaseCount = 1;
    private static readonly SemaphoreSlim FixtureGate = new(ExclusiveLeaseCount, ExclusiveLeaseCount);

    public const string GateEnvironmentVariable = "FOURSUP_FTP";
    public const string EnabledEnvironmentValue = "1";
    public const string ServerUriEnvironmentVariable = "FOURSUP_FTP_URI";
    public const string UserEnvironmentVariable = "FOURSUP_FTP_USER";
    public const string PasswordEnvironmentVariable = "FOURSUP_FTP_PASSWORD";
    public const string FixtureObjectKey = "blob";

    public static FtpObjectStore CreateStore(bool enableSsl = false)
        => new(
            new Uri(GetRequiredEnvironmentVariable(ServerUriEnvironmentVariable)),
            new NetworkCredential(
                GetRequiredEnvironmentVariable(UserEnvironmentVariable),
                GetRequiredEnvironmentVariable(PasswordEnvironmentVariable)),
            enableSsl,
            allowUntrustedCertificateForTesting: enableSsl);

    public static byte[] ReadExpectedFixtureBytes()
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "ftp", "blob"));

    public static async ValueTask<IAsyncDisposable> AcquireFixtureAsync()
    {
        await FixtureGate.WaitAsync();
        return new FixtureLease();
    }

    private static string GetRequiredEnvironmentVariable(string name)
        => Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"The {name} environment variable is required for FTP integration tests.");

    private sealed class FixtureLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            FixtureGate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
