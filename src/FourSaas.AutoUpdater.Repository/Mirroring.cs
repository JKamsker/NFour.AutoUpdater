namespace FourSaas.AutoUpdater.Repository;

public sealed record MirrorResult(int Copied, int Deleted, ImmutableArray<Diagnostic> Diagnostics);
public sealed class MirrorService
{
    public async ValueTask<MirrorResult> MirrorAsync(IListableObjectStore source, IWritableObjectStore destination, bool deleteOrphans = false, string? channel = null, CancellationToken cancellationToken = default)
    {
        if (channel is not null && !Identifier.IsValid(channel, "channel", out var channelError)) throw new FormatException(channelError);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>(); var sourceKeys = new HashSet<ObjectKey>(); var copied = 0; var deleted = 0;
        await foreach (var key in source.ListAsync(cancellationToken: cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (channel is not null && !MatchesChannel(key, channel)) continue;
            sourceKeys.Add(key);
            var sourceObject = await source.OpenAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (sourceObject is null) { diagnostics.Add(new("MIRROR001", DiagnosticSeverity.Error, $"Source object '{key}' disappeared.")); continue; }
            await using (sourceObject.ConfigureAwait(false))
            {
                await using var bytes = new MemoryStream();
                await sourceObject.Content.CopyToAsync(bytes, cancellationToken).ConfigureAwait(false);
                bytes.Position = 0;
                if (TryParseBlobKey(key, out var expected))
                {
                    if (ContentHash.Compute(bytes.ToArray(), expected.Algorithm) != expected) { diagnostics.Add(new("MIRROR002", DiagnosticSeverity.Error, $"Source blob '{key}' failed its content digest.")); continue; }
                    if (!await PutImmutableAsync(destination, key, bytes, expected, cancellationToken).ConfigureAwait(false)) diagnostics.Add(new("MIRROR003", DiagnosticSeverity.Error, $"Destination already contains different bytes for immutable blob '{key}'."));
                }
                else if (IsImmutableArtifact(key))
                {
                    var digest = ContentHash.Compute(bytes.ToArray());
                    if (!await PutImmutableAsync(destination, key, bytes, digest, cancellationToken).ConfigureAwait(false)) diagnostics.Add(new("MIRROR003", DiagnosticSeverity.Error, $"Destination already contains different bytes for immutable artifact '{key}'."));
                }
                else { bytes.Position = 0; await destination.PutAsync(key, bytes, bytes.Length, cancellationToken).ConfigureAwait(false); }
                copied++;
            }
        }
        if (deleteOrphans && destination is IListableObjectStore destinationList)
            await foreach (var key in destinationList.ListAsync(cancellationToken: cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                if ((channel is null || MatchesChannel(key, channel)) && !sourceKeys.Contains(key)) { await destination.DeleteAsync(key, cancellationToken).ConfigureAwait(false); deleted++; }
        return new MirrorResult(copied, deleted, diagnostics.ToImmutable());
    }

    private static bool TryParseBlobKey(ObjectKey key, out ContentHash hash)
    {
        var parts = key.Value.Split('/');
        if (parts.Length == 5 && parts[0] == "blobs" && ContentHash.TryParse(parts[1] + ":" + parts[4], out hash)) return true;
        hash = default; return false;
    }

    private static bool IsImmutableArtifact(ObjectKey key) => key.Value.StartsWith("packages/", StringComparison.Ordinal) || key.Value.EndsWith("/release.lock.json", StringComparison.Ordinal) || key.Value.EndsWith("/coverage.json", StringComparison.Ordinal) || key.Value.EndsWith("/release.bundle.json", StringComparison.Ordinal);

    private static bool MatchesChannel(ObjectKey key, string channel)
    {
        var parts = key.Value.Split('/');
        return parts.Length == 4 && parts[0] == "products" && parts[2] == "channels" && string.Equals(Path.GetFileNameWithoutExtension(parts[3]), channel, StringComparison.Ordinal);
    }

    private static async ValueTask<bool> PutImmutableAsync(IWritableObjectStore destination, ObjectKey key, MemoryStream bytes, ContentHash expected, CancellationToken cancellationToken)
    {
        bytes.Position = 0;
        if (destination is IContentAddressedWriteStore addressed && key.Value.StartsWith("blobs/", StringComparison.Ordinal))
        {
            if (await addressed.PutIfAbsentAsync(key, expected, bytes, bytes.Length, cancellationToken).ConfigureAwait(false)) return true;
        }
        else if (destination is IConditionalWriteStore conditional)
        {
            if (await conditional.PutIfAbsentAsync(key, bytes, bytes.Length, cancellationToken).ConfigureAwait(false)) return true;
        }
        else return false;
        var existing = await destination.OpenAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (existing is null) return false;
        await using (existing.ConfigureAwait(false))
        {
            using var existingBytes = new MemoryStream();
            await existing.Content.CopyToAsync(existingBytes, cancellationToken).ConfigureAwait(false);
            return existingBytes.ToArray().AsSpan().SequenceEqual(bytes.ToArray());
        }
    }
}
