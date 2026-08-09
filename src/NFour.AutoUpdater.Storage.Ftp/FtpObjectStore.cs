using FluentFTP;
using NFour.AutoUpdater.Storage;

namespace NFour.AutoUpdater.Storage.Ftp;

/// <summary>
/// FTP/FTPS transport backed by FluentFTP.  FEAT/MLST/REST support is
/// negotiated by the library after connect; this adapter refuses to enumerate
/// a server that does not advertise machine-readable listings.
/// </summary>
public sealed class FtpObjectStore : IDelimitedObjectStore, IWritableObjectStore, IServerSideTransferStore
{
    private const string FtpScheme = "ftp";
    private const string FtpsScheme = "ftps";
    private const int DefaultControlPort = 21;
    private const int TransportParallelism = 4;
    private const int DisabledRetryCount = 0;
    private const char RemotePathSeparator = '/';
    private const string ValidatorFieldSeparator = ":";
    private const string TemporaryObjectMarker = ".tmp-";
    private const string CompactGuidFormat = "N";

    private readonly Uri _baseUri;
    private readonly NetworkCredential _credentials;
    private readonly bool _enableSsl;
    private readonly bool _allowUntrustedCertificateForTesting;

    /// <summary>Initializes an FTP or explicit-FTPS object store.</summary>
    /// <param name="baseUri">The absolute FTP repository base URI.</param>
    /// <param name="credentials">The credentials supplied to the FTP server.</param>
    /// <param name="enableSsl">Whether explicit TLS is required.</param>
    public FtpObjectStore(Uri baseUri, NetworkCredential credentials, bool enableSsl = true)
        : this(baseUri, credentials, enableSsl, allowUntrustedCertificateForTesting: false)
    {
    }

    internal FtpObjectStore(
        Uri baseUri,
        NetworkCredential credentials,
        bool enableSsl,
        bool allowUntrustedCertificateForTesting)
    {
        if (!baseUri.IsAbsoluteUri || baseUri.Scheme is not (FtpScheme or FtpsScheme))
            throw new ArgumentException("FTP store requires an absolute ftp or ftps URI.", nameof(baseUri));
        _baseUri = baseUri;
        _credentials = credentials;
        _enableSsl = enableSsl;
        _allowUntrustedCertificateForTesting = allowUntrustedCertificateForTesting;
    }

    // FTP has no conditional write or server-side copy primitive.  Rename is
    // used for promotion, but the capability is intentionally not advertised
    // because RNTO cannot provide create-if-absent semantics.
    //
    // Range is likewise not advertised.  REST can splice a transfer, but MDTM+SIZE is the
    // only validator this protocol offers and it is weak, so OpenAsync refuses every ranged
    // request a normal caller could construct.  Advertising a capability no caller can
    // safely exercise would only produce silent restarts.
    /// <inheritdoc />
    public StorageCapabilities Capabilities => StorageCapabilities.Read | StorageCapabilities.List | StorageCapabilities.Write | StorageCapabilities.Delete;
    /// <inheritdoc />
    public int RecommendedParallelism => TransportParallelism;

    /// <inheritdoc />
    public async ValueTask<ReadResult?> OpenAsync(ObjectKey key, long offset = 0, ObjectValidator? ifMatch = null, CancellationToken cancellationToken = default)
    {
        // Ownership of the client transfers to the returned ClientResponseStream on the
        // success path; it must not be scoped with `await using` here, or the control
        // connection would be torn down before the caller reads a byte.
        var client = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        var transferred = false;
        try
        {
            if (!await client.FileExists(RemotePath(key), cancellationToken).ConfigureAwait(false)) return null;

            // MDTM+SIZE is deliberately weak.  Do not splice a partial download
            // unless a caller supplies an independently obtained strong validator.
            if (offset > 0 && ifMatch is not { IsStrong: true }) offset = 0;
            if (offset > 0 && !client.HasFeature(FtpCapability.REST)) offset = 0;
            var stream = await client.OpenRead(RemotePath(key), FtpDataType.Binary, offset, false, cancellationToken).ConfigureAwait(false);
            var content = new ClientResponseStream(stream, client);
            transferred = true;
            return new ReadResult
            {
                Content = content,
                ActualStartOffset = offset,
                StatusCode = (int)(offset == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent),
                Validator = null
            };
        }
        finally
        {
            if (!transferred) await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<ObjectHead?> HeadAsync(ObjectKey key, CancellationToken cancellationToken = default)
    {
        await using var client = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        var path = RemotePath(key);
        if (!await client.FileExists(path, cancellationToken).ConfigureAwait(false)) return null;
        var size = await client.GetFileSize(path, 0, cancellationToken).ConfigureAwait(false);
        var modified = await client.GetModifiedTime(path, cancellationToken).ConfigureAwait(false);
        return new ObjectHead(size, new(ObjectValidatorKind.SizeAndMtime, $"{size}{ValidatorFieldSeparator}{modified.ToUniversalTime().Ticks}", false), LastModified: modified.ToUniversalTime());
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ObjectKey> ListAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var client = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!client.HasFeature(FtpCapability.MLST))
            throw new NotSupportedException("The FTP server does not advertise MLST/MLSD; directory enumeration is not safe without machine-readable listings.");

        var root = string.IsNullOrWhiteSpace(prefix) ? RemoteRoot : RemotePath(prefix!);
        var items = await client.GetListing(root, FtpListOption.Recursive | FtpListOption.SizeModify, cancellationToken).ConfigureAwait(false);
        foreach (var item in items.Where(item => item.Type == FtpObjectType.File).OrderBy(item => item.FullName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = RelativePath(item.FullName);
            if (prefix is null || value.StartsWith(prefix.Trim('/'), StringComparison.Ordinal)) yield return new ObjectKey(value);
        }
    }
    /// <inheritdoc />
    public async IAsyncEnumerable<ObjectListing> ListAsync(string? prefix, string delimiter, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(delimiter)) throw new ArgumentException("A delimiter is required.", nameof(delimiter));
        var root = prefix?.Trim(RemotePathSeparator) ?? string.Empty;
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var key in ListAsync(prefix, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var remainder = key.Value[root.Length..];
            var separator = remainder.IndexOf(delimiter, StringComparison.Ordinal);
            if (separator < 0) yield return new ObjectListing(key, null);
            else if (prefixes.Add(key.Value[..(root.Length + separator + delimiter.Length)])) yield return new ObjectListing(null, key.Value[..(root.Length + separator + delimiter.Length)]);
        }
    }

    /// <inheritdoc />
    public async ValueTask PutAsync(ObjectKey key, Stream content, long? length = null, CancellationToken cancellationToken = default)
    {
        await using var client = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        var destination = RemotePath(key);
        await client.CreateDirectory(ParentPath(destination), true, cancellationToken).ConfigureAwait(false);
        var temporary = destination + TemporaryObjectMarker + Guid.NewGuid().ToString(CompactGuidFormat, CultureInfo.InvariantCulture);
        try
        {
            var status = await client.UploadStream(content, temporary, FtpRemoteExists.Overwrite, true, null, cancellationToken).ConfigureAwait(false);
            if (status is not FtpStatus.Success)
                throw new IOException($"FTP upload of '{key}' failed with status {status}.");
            if (!await client.MoveFile(temporary, destination, FtpRemoteExists.Overwrite, cancellationToken).ConfigureAwait(false))
                throw new IOException($"FTP rename of temporary object '{key}' failed.");
        }
        finally
        {
            try { if (await client.FileExists(temporary, CancellationToken.None).ConfigureAwait(false)) await client.DeleteFile(temporary, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
        }
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(ObjectKey key, CancellationToken cancellationToken = default)
    {
        await using var client = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        var path = RemotePath(key);
        if (await client.FileExists(path, cancellationToken).ConfigureAwait(false)) await client.DeleteFile(path, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryCopyFromAsync(IReadableObjectStore sourceStore, ObjectKey source, ObjectKey destination, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        // When staging and served content share this FTP tree, promotion is an
        // atomic server-side RNTO.  The byte-relay fallback below is reserved
        // for a genuinely separate source store.
        if (sourceStore is FtpObjectStore sourceFtp && CanUseSameServer(sourceFtp))
        {
            await using var client = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            var sourcePath = sourceFtp.RemotePath(source);
            var destinationPath = RemotePath(destination);
            if (!await client.FileExists(sourcePath, cancellationToken).ConfigureAwait(false)) throw new FileNotFoundException(source.Value);
            if (!overwrite && await client.FileExists(destinationPath, cancellationToken).ConfigureAwait(false)) throw new IOException($"Destination '{destination}' already exists.");
            await client.CreateDirectory(ParentPath(destinationPath), true, cancellationToken).ConfigureAwait(false);
            if (!await client.MoveFile(sourcePath, destinationPath, overwrite ? FtpRemoteExists.Overwrite : FtpRemoteExists.Skip, cancellationToken).ConfigureAwait(false))
                throw new IOException($"FTP server-side promotion of '{source}' to '{destination}' failed.");
            return true;
        }
        if (!overwrite && await HeadAsync(destination, cancellationToken).ConfigureAwait(false) is not null)
            throw new IOException($"Destination '{destination}' already exists.");
        var body = await sourceStore.OpenAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (body is null) throw new FileNotFoundException(source.Value);
        await using (body.ConfigureAwait(false)) await PutAsync(destination, body.Content, null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async ValueTask<AsyncFtpClient> ConnectAsync(CancellationToken cancellationToken)
    {
        var config = new FtpConfig
        {
            EncryptionMode = _enableSsl ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None,
            DataConnectionEncryption = _enableSsl,
            RetryAttempts = DisabledRetryCount,
            ValidateAnyCertificate = _allowUntrustedCertificateForTesting
        };
        var client = new AsyncFtpClient(_baseUri.Host, _credentials, _baseUri.Port > 0 ? _baseUri.Port : DefaultControlPort, config);
        try
        {
            await client.Connect(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private string RemoteRoot => _baseUri.AbsolutePath.TrimEnd(RemotePathSeparator);
    private bool CanUseSameServer(FtpObjectStore other)
        => string.Equals(_baseUri.Scheme, other._baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_baseUri.Host, other._baseUri.Host, StringComparison.OrdinalIgnoreCase)
            && _baseUri.Port == other._baseUri.Port
            && string.Equals(_credentials.UserName, other._credentials.UserName, StringComparison.Ordinal)
            && string.Equals(_credentials.Password, other._credentials.Password, StringComparison.Ordinal)
            && _enableSsl == other._enableSsl;
    private string RemotePath(ObjectKey key) => RemotePath(key.Value);
    private string RemotePath(string path) => $"{RemoteRoot}{RemotePathSeparator}{path.TrimStart(RemotePathSeparator)}";
    private string RelativePath(string path)
    {
        var root = RemoteRoot.TrimEnd(RemotePathSeparator) + RemotePathSeparator;
        return path.StartsWith(root, StringComparison.Ordinal) ? path[root.Length..].Trim(RemotePathSeparator) : path.Trim(RemotePathSeparator);
    }
    private static string ParentPath(string path) => path[..path.LastIndexOf(RemotePathSeparator)];

    private sealed class ClientResponseStream(Stream inner, AsyncFtpClient client) : Stream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                client.Dispose();
            }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
