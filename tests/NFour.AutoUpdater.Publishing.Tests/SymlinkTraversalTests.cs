using NFour.AutoUpdater.Core;
using NFour.AutoUpdater.Publishing;

namespace NFour.AutoUpdater.Publishing.Tests;

public sealed class SymlinkTraversalTests
{
    [Fact]
    public async Task OrdinaryFileReachedThroughSymlinkedParentIsNeverPackaged()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "4sup-slice-link-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(testRoot, "source");
        var outside = Path.Combine(testRoot, "outside");
        var link = Path.Combine(source, "linked-parent");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "must not publish");

        try
        {
            await CreateDirectoryLinkAsync(link, outside);
            var result = await new SliceEngine().SliceAsync(new SliceRules
            {
                Source = source,
                Packages =
                [
                    new SlicePackageDefinition
                    {
                        Id = new PackageId("content"),
                        Include = ["**"]
                    }
                ]
            });

            var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "PKG012");
            Assert.Contains("linked-parent", diagnostic.Message, StringComparison.Ordinal);
            Assert.Empty(Assert.Single(result.Packages).Files);
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
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
