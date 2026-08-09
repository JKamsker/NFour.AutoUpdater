using FourSaas.AutoUpdater.Storage;
using FourSaas.AutoUpdater.Storage.Local;

namespace FourSaas.AutoUpdater.Storage.Tests;

public sealed class LocalContainmentTests
{
    [Fact]
    public async Task PlantedDirectoryLinkCannotReadWriteOrEnumerateOutsideRoot()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "4sup-containment-" + Guid.NewGuid().ToString("N"));
        var storeRoot = Path.Combine(testRoot, "store");
        var outside = Path.Combine(testRoot, "outside");
        var link = Path.Combine(storeRoot, "linked");
        Directory.CreateDirectory(storeRoot);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "outside");

        try
        {
            await CreateDirectoryLinkAsync(link, outside);
            await using var store = new LocalObjectStore(storeRoot);
            var error = await Assert.ThrowsAsync<IOException>(async () =>
                await store.OpenAsync(new ObjectKey("linked/secret.txt")));
            Assert.Contains("link inside the object-store root", error.Message, StringComparison.Ordinal);

            await Assert.ThrowsAsync<IOException>(async () =>
                await store.PutAsync(new ObjectKey("linked/new.txt"), new MemoryStream("escape"u8.ToArray())));

            Assert.Equal("outside", await File.ReadAllTextAsync(Path.Combine(outside, "secret.txt")));
            Assert.False(File.Exists(Path.Combine(outside, "new.txt")));
            Assert.Empty(await ListAsync(store));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task<ObjectKey[]> ListAsync(IListableObjectStore store)
    {
        var keys = new List<ObjectKey>();
        await foreach (var key in store.ListAsync()) keys.Add(key);
        return keys.ToArray();
    }

    private static async Task CreateDirectoryLinkAsync(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target })
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Unable to start mklink.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new IOException("Unable to create test junction: " + await process.StandardError.ReadToEndAsync());
    }
}
