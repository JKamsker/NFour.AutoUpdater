using FourSaas.AutoUpdater.Storage.Ftp;
using System.Net;

namespace FourSaas.AutoUpdater.Storage.Tests;

internal static class FtpIntegrationSettings
{
    public const string GateEnvironmentVariable = "FOURSUP_FTP";
    public const string EnabledEnvironmentValue = "1";
    public const string ServerUriEnvironmentVariable = "FOURSUP_FTP_URI";
    public const string UserEnvironmentVariable = "FOURSUP_FTP_USER";
    public const string PasswordEnvironmentVariable = "FOURSUP_FTP_PASSWORD";
    public const string FixtureObjectKey = "blob";

    public static FtpObjectStore CreateStore()
        => new(
            new Uri(GetRequiredEnvironmentVariable(ServerUriEnvironmentVariable)),
            new NetworkCredential(
                GetRequiredEnvironmentVariable(UserEnvironmentVariable),
                GetRequiredEnvironmentVariable(PasswordEnvironmentVariable)),
            enableSsl: false);

    public static byte[] ReadExpectedFixtureBytes()
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "ftp", "blob"));

    private static string GetRequiredEnvironmentVariable(string name)
        => Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"The {name} environment variable is required for FTP integration tests.");
}
