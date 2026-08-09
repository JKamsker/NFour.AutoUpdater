using Amazon.S3;
using NFour.AutoUpdater.Core;
using NFour.AutoUpdater.Storage;
using NFour.AutoUpdater.Storage.S3;
using System.Text;

namespace NFour.AutoUpdater.Storage.S3.Tests;

public sealed class S3Tests
{
    private const string IntegrationPrefix = "integration";
    private const string RoundTripObjectKey = "blobs/sha256/00/00/minio-test";
    private const string RoundTripContent = "minio-content";
    private const string ExpectedRangedContent = "content";
    private const long RangeStartOffset = 6;

    [Theory]
    [InlineData(S3ProviderProfile.Aws, true)]
    [InlineData(S3ProviderProfile.Minio, true)]
    [InlineData(S3ProviderProfile.R2, true)]
    [InlineData(S3ProviderProfile.B2, false)]
    [InlineData(S3ProviderProfile.Generic, false)]
    public async Task ProviderProfilesExposeTheirDeclaredIntegritySurface(S3ProviderProfile profile, bool conditional)
    {
        using var client = new AmazonS3Client(new AmazonS3Config { ServiceURL = "http://localhost:9000", ForcePathStyle = true });
        await using S3ObjectStore store = conditional
            ? new S3ConditionalObjectStore("bucket", client: client, providerProfile: profile)
            : new S3ObjectStore("bucket", client: client, providerProfile: profile);
        Assert.Equal(conditional, store.Capabilities.HasFlag(StorageCapabilities.ConditionalWrite));
        Assert.Equal(conditional, store.UploadDigestIsStorageEnforced);
        Assert.True(StorageCapabilityNegotiation.IsConsistent(store));
    }

    [Fact]
    public async Task ProviderProfileDoesNotAssumeBackblazeConditionalWrites()
    {
        using var client = new AmazonS3Client(new AmazonS3Config { ServiceURL = "http://localhost:9000", ForcePathStyle = true });
        await using var store = new S3ObjectStore("bucket", client: client, providerProfile: S3ProviderProfile.B2);
        Assert.Equal(S3ProviderProfile.B2, store.ProviderProfile);
        Assert.False(store.Capabilities.HasFlag(StorageCapabilities.ConditionalWrite));
        Assert.True(StorageCapabilityNegotiation.IsConsistent(store));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await store.PutIfAbsentAsync(new ObjectKey("object"), new MemoryStream("bytes"u8.ToArray())));
        var headers = store.GetRequiredUploadHeaders(new UploadGrantDescriptor(new ObjectKey("_staging/x"), ContentHash.Compute("x"u8), 1, DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.DoesNotContain("x-amz-checksum-sha256", headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("If-None-Match", headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("1", headers["Content-Length"]);

        using var conditionalClient = new AmazonS3Client(new AmazonS3Config { ServiceURL = "http://localhost:9000", ForcePathStyle = true });
        await using var conditional = new S3ConditionalObjectStore("bucket", client: conditionalClient, providerProfile: S3ProviderProfile.Minio);
        Assert.True(conditional.Capabilities.HasFlag(StorageCapabilities.ConditionalWrite));
        Assert.True(StorageCapabilityNegotiation.IsConsistent(conditional));
    }

    [Fact]
    public async Task MinioRoundTripAndRange()
    {
        MinioIntegrationSettings.SkipUnlessEnabled();
        await using var store = MinioIntegrationSettings.CreateStore(IntegrationPrefix);
        var key = new ObjectKey(RoundTripObjectKey); var bytes = Encoding.UTF8.GetBytes(RoundTripContent);
        await store.PutAsync(key, new MemoryStream(bytes), bytes.Length);
        var head = await store.HeadAsync(key); Assert.NotNull(head); Assert.Equal(bytes.Length, head!.Length);
        var response = await store.OpenAsync(key, RangeStartOffset); Assert.NotNull(response);
        await using (response!)
        {
            using var output = new MemoryStream();
            await response.Content.CopyToAsync(output);
            Assert.Equal(ExpectedRangedContent, Encoding.UTF8.GetString(output.ToArray()));
        }
        await store.DeleteAsync(key);
    }
}
