#pragma warning disable SYSLIB0014
using FourSaas.AutoUpdater.Storage;

namespace FourSaas.AutoUpdater.Storage.Ftp;

/// <summary>A deliberately small FTP transport. FTP is a write transport in the recommended topology; reads should use HttpObjectStore.</summary>
public sealed class FtpObjectStore : IListableObjectStore, IWritableObjectStore
{
    private readonly Uri _baseUri;
    private readonly NetworkCredential _credentials;
    private readonly bool _enableSsl;
    public FtpObjectStore(Uri baseUri, NetworkCredential credentials, bool enableSsl = true) { _baseUri = baseUri; _credentials = credentials; _enableSsl = enableSsl; }
    public StorageCapabilities Capabilities => StorageCapabilities.Read | StorageCapabilities.Range | StorageCapabilities.List | StorageCapabilities.Write | StorageCapabilities.Delete;
    public int RecommendedParallelism => 4;
    public async ValueTask<ReadResult?> OpenAsync(ObjectKey key, long offset = 0, ObjectValidator? ifMatch = null, CancellationToken cancellationToken = default)
    {
        // FTP has only a weak MDTM+SIZE validator. Never append to an
        // unguarded or weakly guarded partial object.
        if (offset > 0 && ifMatch is not { IsStrong: true }) offset = 0;
        var request = Create(key, WebRequestMethods.Ftp.DownloadFile); if (offset > 0) request.ContentOffset = offset;
        try { var response = (FtpWebResponse)await request.GetResponseAsync().WaitAsync(cancellationToken).ConfigureAwait(false); return new ReadResult { Content = new ResponseStream(response), ActualStartOffset = offset, StatusCode = offset == 0 ? 200 : 206, Validator = null }; }
        catch (WebException ex) when (ex.Response is FtpWebResponse ftp && ftp.StatusCode == FtpStatusCode.ActionNotTakenFileUnavailable) { ex.Response.Dispose(); return null; }
    }
    public async ValueTask<ObjectHead?> HeadAsync(ObjectKey key, CancellationToken cancellationToken = default)
    {
        var sizeRequest = Create(key, WebRequestMethods.Ftp.GetFileSize); try { using var sizeResponse = (FtpWebResponse)await sizeRequest.GetResponseAsync().WaitAsync(cancellationToken).ConfigureAwait(false); var modifiedRequest = Create(key, WebRequestMethods.Ftp.GetDateTimestamp); using var modified = (FtpWebResponse)await modifiedRequest.GetResponseAsync().WaitAsync(cancellationToken).ConfigureAwait(false); return new ObjectHead(sizeResponse.ContentLength, new(ObjectValidatorKind.SizeAndMtime, $"{sizeResponse.ContentLength}:{modified.LastModified.Ticks}", false), LastModified: modified.LastModified.ToUniversalTime()); } catch (WebException ex) when (ex.Response is FtpWebResponse ftp && ftp.StatusCode == FtpStatusCode.ActionNotTakenFileUnavailable) { ex.Response.Dispose(); return null; }
    }
    public async IAsyncEnumerable<ObjectKey> ListAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var pending = new Queue<string>([prefix?.Trim('/') ?? string.Empty]);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Dequeue();
            var request = CreatePath(directory, "MLSD");
            using var response = (FtpWebResponse)await request.GetResponseAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(response.GetResponseStream());
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                var separator = line.IndexOf("; ", StringComparison.Ordinal);
                var name = separator >= 0 ? line[(separator + 2)..].Trim() : line.Trim();
                if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.EndsWith(';')) continue;
                var full = string.IsNullOrEmpty(directory) ? name : directory + "/" + name;
                var type = line.Split(';', 2)[0];
                if (type.Contains("type=dir", StringComparison.OrdinalIgnoreCase)) pending.Enqueue(full);
                else yield return new ObjectKey(full);
            }
        }
    }
    public async ValueTask PutAsync(ObjectKey key, Stream content, long? length = null, CancellationToken cancellationToken = default)
    {
        var temporary = new ObjectKey(key.Value + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            var request = Create(temporary, WebRequestMethods.Ftp.UploadFile);
            if (length is { } expectedLength) request.ContentLength = expectedLength;
            await using (var output = await request.GetRequestStreamAsync().WaitAsync(cancellationToken).ConfigureAwait(false)) await content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            using (var response = (FtpWebResponse)await request.GetResponseAsync().WaitAsync(cancellationToken).ConfigureAwait(false)) { }
            var rename = Create(temporary, WebRequestMethods.Ftp.Rename); rename.RenameTo = key.Value;
            using (var response = (FtpWebResponse)await rename.GetResponseAsync().WaitAsync(cancellationToken).ConfigureAwait(false)) { }
        }
        catch
        {
            try { using var cleanup = (FtpWebResponse)await Create(temporary, WebRequestMethods.Ftp.DeleteFile).GetResponseAsync().WaitAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            throw;
        }
    }
    public async ValueTask DeleteAsync(ObjectKey key, CancellationToken cancellationToken = default) { using var response = (FtpWebResponse)await Create(key, WebRequestMethods.Ftp.DeleteFile).GetResponseAsync().WaitAsync(cancellationToken).ConfigureAwait(false); }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private FtpWebRequest Create(ObjectKey key, string method) => CreatePath(key.Value, method);
    private FtpWebRequest CreatePath(string path, string method) { var request = (FtpWebRequest)WebRequest.Create(new Uri(_baseUri, path.Trim('/'))); request.Method = method; request.Credentials = _credentials; request.EnableSsl = _enableSsl; request.UseBinary = true; request.KeepAlive = false; return request; }
    private sealed class ResponseStream(FtpWebResponse response) : Stream
    {
        private readonly Stream _inner = response.GetResponseStream();
        protected override void Dispose(bool disposing) { if (disposing) { _inner.Dispose(); response.Dispose(); } base.Dispose(disposing); }
        public override ValueTask DisposeAsync() { _inner.Dispose(); response.Dispose(); return ValueTask.CompletedTask; }
        public override bool CanRead => _inner.CanRead; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => _inner.Length; public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush(); public override int Read(byte[] b, int o, int c) => _inner.Read(b, o, c); public override int Read(Span<byte> b) => _inner.Read(b); public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken ct) => _inner.ReadAsync(b, o, c, ct); public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default) => _inner.ReadAsync(b, ct); public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException(); public override void Write(ReadOnlySpan<byte> b) => throw new NotSupportedException(); public override Task WriteAsync(byte[] b, int o, int c, CancellationToken ct) => throw new NotSupportedException(); public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
#pragma warning restore SYSLIB0014
