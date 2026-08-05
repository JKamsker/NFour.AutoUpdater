using FourSaas.AutoUpdater.Core;
using FourSaas.AutoUpdater.Storage;
using FourSaas.AutoUpdater.Storage.Local;
using FourSaas.AutoUpdater.Storage.Memory;
using System.Text;

namespace FourSaas.AutoUpdater.Storage.Tests;

public sealed class StorageTests
{
    public static IEnumerable<object[]> Stores()
    {
        yield return [new Func<string, IReadableObjectStore>(_ => new MemoryObjectStore())];
        yield return [new Func<string, IReadableObjectStore>(root => new LocalObjectStore(root))];
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ReadWriteRangeAndHeadAreConsistent(Func<string, IReadableObjectStore> factory)
    {
        var root = Path.Combine(Path.GetTempPath(), "4sup-test-" + Guid.NewGuid().ToString("N"));
        await using var store = factory(root);
        var writable = Assert.IsAssignableFrom<IWritableObjectStore>(store);
        var bytes = Encoding.UTF8.GetBytes("0123456789");
        await writable.PutAsync(new ObjectKey("blobs/sha256/00/00/a"), new MemoryStream(bytes));
        var head = await store.HeadAsync(new ObjectKey("blobs/sha256/00/00/a"));
        Assert.NotNull(head); Assert.Equal(bytes.Length, head!.Length);
        var read = await store.OpenAsync(new ObjectKey("blobs/sha256/00/00/a"), 4);
        Assert.NotNull(read);
        await using (read!)
        {
            using var destination = new MemoryStream();
            await read.Content.CopyToAsync(destination);
            Assert.Equal("456789", Encoding.UTF8.GetString(destination.ToArray()));
        }
        if (store is IListableObjectStore list) Assert.Contains("blobs/sha256/00/00/a", await ToArrayAsync(list.ListAsync()));
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static async Task<string[]> ToArrayAsync(IAsyncEnumerable<ObjectKey> keys) { var result = new List<string>(); await foreach (var key in keys) result.Add(key.Value); return result.ToArray(); }
}
