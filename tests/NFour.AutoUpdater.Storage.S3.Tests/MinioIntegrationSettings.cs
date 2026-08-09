using Amazon.S3;
using NFour.AutoUpdater.Storage.S3;

namespace NFour.AutoUpdater.Storage.S3.Tests;

internal static class MinioIntegrationSettings
{
    public const string GateEnvironmentVariable = "FOURSUP_MINIO";
    public const string EnabledEnvironmentValue = "1";
    public const string ServiceUriEnvironmentVariable = "FOURSUP_MINIO_URI";
    public const string AccessKeyEnvironmentVariable = "FOURSUP_MINIO_ACCESS_KEY";
    public const string SecretKeyEnvironmentVariable = "FOURSUP_MINIO_SECRET_KEY";
    public const string Bucket = "4sup-test";

    public static void SkipUnlessEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(GateEnvironmentVariable), EnabledEnvironmentValue, StringComparison.Ordinal))
            Assert.Skip($"Set {GateEnvironmentVariable}={EnabledEnvironmentValue} after starting docker compose to run MinIO integration tests.");
    }

    public static AmazonS3Config CreateClientConfiguration()
        => new()
        {
            ServiceURL = GetRequiredEnvironmentVariable(ServiceUriEnvironmentVariable),
            ForcePathStyle = true
        };

    public static AmazonS3Client CreateClient()
        => new(
            GetRequiredEnvironmentVariable(AccessKeyEnvironmentVariable),
            GetRequiredEnvironmentVariable(SecretKeyEnvironmentVariable),
            CreateClientConfiguration());

    public static S3ObjectStore CreateStore(string prefix)
        => new(
            Bucket,
            prefix,
            serviceUrl: new Uri(GetRequiredEnvironmentVariable(ServiceUriEnvironmentVariable)),
            accessKey: GetRequiredEnvironmentVariable(AccessKeyEnvironmentVariable),
            secretKey: GetRequiredEnvironmentVariable(SecretKeyEnvironmentVariable),
            providerProfile: S3ProviderProfile.Minio);

    public static string GetRequiredEnvironmentVariable(string name)
        => Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"The {name} environment variable is required for MinIO integration tests.");
}
