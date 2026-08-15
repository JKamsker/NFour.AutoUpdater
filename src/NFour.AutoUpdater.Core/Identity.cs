namespace NFour.AutoUpdater.Core;


/// <summary>Identifies a content-digest algorithm.</summary>
public enum HashAlgorithmId
{
    /// <summary>SHA-256.</summary>
    Sha256 = 1,
    /// <summary>SHA-512.</summary>
    Sha512 = 2,
    /// <summary>MD5, supported only where required for storage interoperability.</summary>
    Md5 = 3,
    /// <summary>BLAKE3, reserved for documents but not implemented by the base runtime.</summary>
    Blake3 = 4
}

/// <summary>Represents an immutable, algorithm-qualified content digest.</summary>
public readonly struct ContentHash : IEquatable<ContentHash>, IComparable<ContentHash>
{
    private const int StreamBufferSize = 128 * 1024;
    private const int Sha256DigestLength = 32;
    private const int Sha512DigestLength = 64;
    private const int Md5DigestLength = 16;
    private const string Sha256Name = "sha256";
    private const string Sha512Name = "sha512";
    private const string Md5Name = "md5";
    private const string Blake3Name = "blake3";
    private readonly byte[]? _bytes;

    /// <summary>Initializes a digest from bytes that are copied into immutable storage.</summary>
    /// <param name="algorithm">The digest algorithm.</param>
    /// <param name="value">The digest bytes.</param>
    public ContentHash(HashAlgorithmId algorithm, ReadOnlySpan<byte> value)
    {
        Algorithm = algorithm;
        _bytes = value.ToArray();
        Validate(algorithm, _bytes);
    }

    /// <summary>Initializes a digest from memory that is copied into immutable storage.</summary>
    /// <param name="algorithm">The digest algorithm.</param>
    /// <param name="value">The digest bytes.</param>
    public ContentHash(HashAlgorithmId algorithm, ReadOnlyMemory<byte> value)
        : this(algorithm, value.Span) { }

    /// <summary>Gets the digest algorithm.</summary>
    public HashAlgorithmId Algorithm { get; }

    /// <summary>
    /// The digest bytes, as a span that cannot be written through and cannot be unwrapped to
    /// the backing array. Prefer this over <see cref="Value"/> everywhere inside the solution.
    /// </summary>
    /// <remarks>
    /// JsonIgnore is required, not cosmetic: System.Text.Json reflects over public properties
    /// and throws for a ref struct, so without it any type containing a ContentHash fails to
    /// serialize.
    /// </remarks>
    [JsonIgnore]
    public ReadOnlySpan<byte> Span => _bytes ?? [];

    /// <summary>
    /// The digest bytes as a defensive copy.
    /// </summary>
    /// <remarks>
    /// This used to wrap the internal array directly. ReadOnlyMemory can be unwrapped with
    /// MemoryMarshal.TryGetArray, so a caller could reach the backing store and mutate a value
    /// that had already been used as a dictionary key or had already passed verification —
    /// changing what a "verified" hash means after the fact. The copy makes that impossible;
    /// callers that only need to read should use <see cref="Span"/> and pay nothing.
    /// </remarks>
    public ReadOnlyMemory<byte> Value => _bytes is null ? ReadOnlyMemory<byte>.Empty : _bytes.AsSpan().ToArray();

    /// <summary>Gets whether the value contains a digest of the required length.</summary>
    public bool IsValid => _bytes is not null && IsValidLength(Algorithm, _bytes.Length);

    /// <summary>Computes a content digest over an in-memory value.</summary>
    /// <param name="bytes">The content to hash.</param>
    /// <param name="algorithm">The digest algorithm.</param>
    /// <returns>The computed digest.</returns>
    public static ContentHash Compute(ReadOnlySpan<byte> bytes, HashAlgorithmId algorithm = HashAlgorithmId.Sha256)
    {
        var result = algorithm switch
        {
            HashAlgorithmId.Sha256 => SHA256.HashData(bytes),
            HashAlgorithmId.Sha512 => SHA512.HashData(bytes),
            HashAlgorithmId.Md5 => MD5.HashData(bytes),
            _ => throw new NotSupportedException($"Hash algorithm {algorithm} is not available in the base implementation.")
        };
        return new ContentHash(algorithm, result);
    }

    /// <summary>Computes a content digest while consuming a stream.</summary>
    /// <param name="source">The stream to consume.</param>
    /// <param name="algorithm">The digest algorithm.</param>
    /// <param name="cancellationToken">Cancels stream consumption.</param>
    /// <returns>The computed digest.</returns>
    public static async ValueTask<ContentHash> ComputeAsync(Stream source, HashAlgorithmId algorithm = HashAlgorithmId.Sha256, CancellationToken cancellationToken = default)
    {
        using var hash = algorithm switch
        {
            HashAlgorithmId.Sha256 => IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
            HashAlgorithmId.Sha512 => IncrementalHash.CreateHash(HashAlgorithmName.SHA512),
            HashAlgorithmId.Md5 => IncrementalHash.CreateHash(HashAlgorithmName.MD5),
            _ => throw new NotSupportedException($"Hash algorithm {algorithm} is not available in the base implementation.")
        };
        var buffer = new byte[StreamBufferSize];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            hash.AppendData(buffer, 0, read);
        return new ContentHash(algorithm, hash.GetHashAndReset());
    }

    /// <summary>Hashes a source stream once while producing the two publish-time digests.</summary>
    public static async ValueTask<(ContentHash Sha256, ContentHash Md5)> ComputeSha256AndMd5Async(Stream source, CancellationToken cancellationToken = default)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                sha256.AppendData(buffer, 0, read);
                md5.AppendData(buffer, 0, read);
            }
            return (new ContentHash(HashAlgorithmId.Sha256, sha256.GetHashAndReset()), new ContentHash(HashAlgorithmId.Md5, md5.GetHashAndReset()));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>Parses an algorithm-qualified lowercase hexadecimal digest.</summary>
    /// <param name="value">The text in <c>algorithm:hex</c> form.</param>
    /// <returns>The parsed digest.</returns>
    public static ContentHash Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
            throw new FormatException("A content hash must use the form algorithm:lowercase-hex.");
        var algorithm = value[..separator] switch
        {
            Sha256Name => HashAlgorithmId.Sha256,
            Sha512Name => HashAlgorithmId.Sha512,
            Md5Name => HashAlgorithmId.Md5,
            Blake3Name => HashAlgorithmId.Blake3,
            _ => (HashAlgorithmId)(-1)
        };
        if ((int)algorithm < 0)
            throw new FormatException($"Unknown hash algorithm '{value[..separator]}'.");
        // Blake3 has a name and a digest length here but no implementation behind Compute.
        // Parsing it would mint a hash that every verification path then refuses to evaluate,
        // so it is rejected at the boundary instead - fail closed, not halfway.
        if (algorithm == HashAlgorithmId.Blake3)
            throw new NotSupportedException("Hash algorithm 'blake3' is declared but not implemented; documents using it cannot be verified.");
        var hex = value[(separator + 1)..];
        if (hex.Length % 2 != 0 || hex.Any(static c => !Uri.IsHexDigit(c)) || hex.Any(char.IsUpper))
            throw new FormatException("A content hash must contain lowercase hexadecimal bytes.");
        var bytes = Convert.FromHexString(hex);
        return new ContentHash(algorithm, bytes);
    }

    /// <summary>
    /// Non-throwing parse. Null, empty and whitespace all return false rather than throwing:
    /// <see cref="Parse"/> raises <see cref="ArgumentException"/> for those, which is not a
    /// <see cref="FormatException"/>, so they would otherwise escape a "Try" method.
    /// </summary>
    public static bool TryParse(string? value, out ContentHash hash)
    {
        hash = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            hash = Parse(value);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException)
        {
            hash = default;
            return false;
        }
    }

    /// <summary>Returns the algorithm-qualified lowercase hexadecimal digest.</summary>
    /// <returns>The canonical digest text.</returns>
    public override string ToString() => IsValid
        ? $"{AlgorithmName(Algorithm)}:{Convert.ToHexString(Span).ToLowerInvariant()}"
        : throw new InvalidOperationException("The default ContentHash is invalid.");

    /// <inheritdoc />
    public bool Equals(ContentHash other) => Algorithm == other.Algorithm && Span.SequenceEqual(other.Span);
    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ContentHash other && Equals(other);
    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Algorithm);
        foreach (var b in Span) hash.Add(b);
        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public int CompareTo(ContentHash other)
    {
        var algorithm = Algorithm.CompareTo(other.Algorithm);
        return algorithm != 0 ? algorithm : Span.SequenceCompareTo(other.Span);
    }

    /// <summary>Determines whether two content hashes are equal.</summary>
    public static bool operator ==(ContentHash left, ContentHash right) => left.Equals(right);
    /// <summary>Determines whether two content hashes differ.</summary>
    public static bool operator !=(ContentHash left, ContentHash right) => !left.Equals(right);

    private static string AlgorithmName(HashAlgorithmId algorithm) => algorithm switch
    {
        HashAlgorithmId.Sha256 => Sha256Name,
        HashAlgorithmId.Sha512 => Sha512Name,
        HashAlgorithmId.Md5 => Md5Name,
        HashAlgorithmId.Blake3 => Blake3Name,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
    };

    private static void Validate(HashAlgorithmId algorithm, byte[] value)
    {
        if (!IsValidLength(algorithm, value.Length))
            throw new ArgumentException($"{algorithm} digest has an invalid length of {value.Length} bytes.", nameof(value));
    }

    private static bool IsValidLength(HashAlgorithmId algorithm, int length) => algorithm switch
    {
        HashAlgorithmId.Sha256 or HashAlgorithmId.Blake3 => length == Sha256DigestLength,
        HashAlgorithmId.Md5 => length == Md5DigestLength,
        HashAlgorithmId.Sha512 => length == Sha512DigestLength,
        _ => false
    };
}

/// <summary>Locates a blob by immutable digest and declared length.</summary>
/// <param name="Content">The blob content digest.</param>
/// <param name="Size">
/// The manifest-declared length of the blob.  The downloader needs this to bound a transfer:
/// without it, a mirror can stream indefinitely and the mismatch is only discovered once the
/// stream ends, by which point the disk is already full.
/// </param>
public readonly record struct BlobLocator(ContentHash Content, long Size)
{
    /// <summary>Returns the content digest in canonical form.</summary>
    /// <returns>The algorithm-qualified digest.</returns>
    public override string ToString() => Content.ToString();
}
