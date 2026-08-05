using FourSaas.AutoUpdater.Storage;
using FourSaas.AutoUpdater.Storage.S3;
using System.Text;

namespace FourSaas.AutoUpdater.Storage.S3.Tests;

public sealed class S3Tests
{
    [Fact]
    public async Task MinioRoundTripAndRange()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("FOURSUP_MINIO"), "1", StringComparison.Ordinal)) Assert.Skip("Set FOURSUP_MINIO=1 after starting docker compose to run MinIO integration tests.");
        await using var store = new S3ObjectStore("4sup-test", "integration", serviceUrl: new Uri("http://localhost:9000"), accessKey: "4sup-test", secretKey: "4sup-test-password");
        var key = new ObjectKey("blobs/sha256/00/00/minio-test"); var bytes = Encoding.UTF8.GetBytes("minio-content");
        await store.PutAsync(key, new MemoryStream(bytes), bytes.Length);
        var head = await store.HeadAsync(key); Assert.NotNull(head); Assert.Equal(bytes.Length, head!.Length);
        var response = await store.OpenAsync(key, 6); Assert.NotNull(response);
        await using (response!)
        {
            using var output = new MemoryStream();
            await response.Content.CopyToAsync(output);
            Assert.Equal("content", Encoding.UTF8.GetString(output.ToArray()));
        }
        await store.DeleteAsync(key);
    }
}
