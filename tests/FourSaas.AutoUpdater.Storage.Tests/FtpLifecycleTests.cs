using FourSaas.AutoUpdater.Storage;

namespace FourSaas.AutoUpdater.Storage.Tests;

[Collection(FtpIntegrationCollection.Name)]
public sealed class FtpLifecycleTests
{
    private const int CancellationProbeBufferLength = 1;

    [Fact]
    public async Task ReadRemainsUsableAfterOpenReturns()
    {
        SkipUnlessEnabled();

        await using var store = FtpIntegrationSettings.CreateStore();

        var read = await store.OpenAsync(new ObjectKey(FtpIntegrationSettings.FixtureObjectKey));
        Assert.NotNull(read);
        await Task.Yield();
        await using (read!)
        {
            using var output = new MemoryStream();
            await read.Content.CopyToAsync(output);
            Assert.Equal(FtpIntegrationSettings.ReadExpectedFixtureBytes(), output.ToArray());
        }
    }

    [Fact]
    public async Task ReadHonorsCallerCancellation()
    {
        SkipUnlessEnabled();

        await using var store = FtpIntegrationSettings.CreateStore();
        var read = await store.OpenAsync(new ObjectKey(FtpIntegrationSettings.FixtureObjectKey));
        Assert.NotNull(read);
        await using (read!)
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancellationProbe = new byte[CancellationProbeBufferLength];
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => read.Content.ReadAsync(cancellationProbe, cancellation.Token).AsTask());
        }
    }

    [Fact]
    public async Task ServerWithoutMachineReadableListingsFailsClosed()
    {
        SkipUnlessEnabled();

        await using var store = FtpIntegrationSettings.CreateStore();
        var head = await store.HeadAsync(new ObjectKey(FtpIntegrationSettings.FixtureObjectKey));
        Assert.NotNull(head);
        Assert.Equal(FtpIntegrationSettings.ReadExpectedFixtureBytes().LongLength, head!.Length);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in store.ListAsync()) { }
        });
    }

    private static void SkipUnlessEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(FtpIntegrationSettings.GateEnvironmentVariable), FtpIntegrationSettings.EnabledEnvironmentValue, StringComparison.Ordinal))
            Assert.Skip($"Set {FtpIntegrationSettings.GateEnvironmentVariable}={FtpIntegrationSettings.EnabledEnvironmentValue} after starting docker compose to run FTP integration tests.");
    }
}
