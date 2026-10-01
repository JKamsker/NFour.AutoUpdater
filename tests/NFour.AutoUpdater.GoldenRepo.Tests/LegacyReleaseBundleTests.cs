using NFour.AutoUpdater.Core;
using NFour.AutoUpdater.Repository;
using NFour.AutoUpdater.Storage;
using NFour.AutoUpdater.Storage.Memory;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace NFour.AutoUpdater.GoldenRepo.Tests;

/// <summary>
/// Bundles published before the exact-bytes encoding embedded each inline manifest as a JSON
/// object under the same schemaVersion. Readers must keep accepting those repositories while
/// still holding every manifest to its pinned digest.
/// </summary>
public sealed class LegacyReleaseBundleTests
{
    private const string Product = "alpha.product";
    private const string Release = "alpha-1";
    private static readonly Dictionary<string, byte[]> Trusted = new()
    {
        ["fixture-root"] = Base64Url.Decode("ebVWLo_mVPlAeLES6KmLp5AfhTrmlb7X4OORC60ElmQ")
    };

    [Fact]
    public async Task ReaderAcceptsLegacyObjectEncodedInlineManifests()
    {
        await using var store = await GoldenRepoTests.LoadGoldenStoreAsync();
        var (reader, layout) = await OpenAsync(store);
        await RewriteInlineAsync(store, layout, manifestBytes => JsonNode.Parse(manifestBytes)!);

        var verified = await reader.ReadReleaseAsync(Product, Release, Trusted);

        Assert.NotNull(verified.Bundle);
        Assert.Equal(verified.Lock.Packages.Length, verified.Bundle!.Inline.Count);
    }

    [Fact]
    public async Task ReaderRejectsLegacyInlineManifestThatDiffersFromItsPin()
    {
        await using var store = await GoldenRepoTests.LoadGoldenStoreAsync();
        var (reader, layout) = await OpenAsync(store);
        await RewriteInlineAsync(store, layout, manifestBytes =>
        {
            var manifest = JsonNode.Parse(manifestBytes)!;
            manifest["installSize"] = manifest["installSize"]!.GetValue<long>() + 1;
            return manifest;
        });

        var error = await Assert.ThrowsAsync<CryptographicException>(() => reader.ReadReleaseAsync(Product, Release, Trusted).AsTask());
        Assert.Contains("failed its pinned digest", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReaderRejectsInlineManifestThatIsNeitherStringNorObject()
    {
        await using var store = await GoldenRepoTests.LoadGoldenStoreAsync();
        var (reader, layout) = await OpenAsync(store);
        await RewriteInlineAsync(store, layout, _ => JsonValue.Create(42));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadReleaseAsync(Product, Release, Trusted).AsTask());
        Assert.Contains("must be a base64url string", error.Message, StringComparison.Ordinal);
    }

    private static async Task<(RepositoryReader Reader, RepositoryLayout Layout)> OpenAsync(MemoryObjectStore store)
    {
        var (descriptor, layout) = await RepositoryFactory.LoadDescriptorAsync(store);
        return (new RepositoryReader(store, descriptor), layout);
    }

    private static async Task RewriteInlineAsync(MemoryObjectStore store, RepositoryLayout layout, Func<byte[], JsonNode> encode)
    {
        var key = layout.ReleaseBundle(Product, Release);
        var bundle = JsonNode.Parse(await GoldenRepoTests.ReadObjectAsync(store, key))!.AsObject();
        var inline = bundle["inline"]!.AsObject();
        foreach (var (packageId, value) in inline.ToArray())
            inline[packageId] = encode(Base64Url.Decode(value!.GetValue<string>()));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(bundle);
        await store.PutAsync(key, new MemoryStream(bytes), bytes.LongLength);
    }
}
