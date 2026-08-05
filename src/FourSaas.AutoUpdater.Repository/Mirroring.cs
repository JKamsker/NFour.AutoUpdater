namespace FourSaas.AutoUpdater.Repository;

public sealed record MirrorResult(int Copied, int Deleted, ImmutableArray<Diagnostic> Diagnostics);
public sealed class MirrorService
{
    public async ValueTask<MirrorResult> MirrorAsync(IListableObjectStore source, IWritableObjectStore destination, bool deleteOrphans = false, CancellationToken cancellationToken = default)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>(); var sourceKeys = new HashSet<ObjectKey>(); var copied = 0; var deleted = 0;
        await foreach (var key in source.ListAsync(cancellationToken: cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            sourceKeys.Add(key);
            var sourceObject = await source.OpenAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (sourceObject is null) { diagnostics.Add(new("MIRROR001", DiagnosticSeverity.Error, $"Source object '{key}' disappeared.")); continue; }
            await using (sourceObject.ConfigureAwait(false))
            {
                if (TryParseBlobKey(key, out var expected))
                {
                    await using var bytes = new MemoryStream(); await sourceObject.Content.CopyToAsync(bytes, cancellationToken).ConfigureAwait(false);
                    if (ContentHash.Compute(bytes.ToArray(), expected.Algorithm) != expected) { diagnostics.Add(new("MIRROR002", DiagnosticSeverity.Error, $"Source blob '{key}' failed its content digest.")); continue; }
                    bytes.Position = 0; await destination.PutAsync(key, bytes, bytes.Length, cancellationToken).ConfigureAwait(false);
                }
                else await destination.PutAsync(key, sourceObject.Content, cancellationToken: cancellationToken).ConfigureAwait(false);
                copied++;
            }
        }
        if (deleteOrphans && destination is IListableObjectStore destinationList)
            await foreach (var key in destinationList.ListAsync(cancellationToken: cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                if (!sourceKeys.Contains(key)) { await destination.DeleteAsync(key, cancellationToken).ConfigureAwait(false); deleted++; }
        return new MirrorResult(copied, deleted, diagnostics.ToImmutable());
    }

    private static bool TryParseBlobKey(ObjectKey key, out ContentHash hash)
    {
        var parts = key.Value.Split('/');
        if (parts.Length == 5 && parts[0] == "blobs" && ContentHash.TryParse(parts[1] + ":" + parts[4], out hash)) return true;
        hash = default; return false;
    }
}
