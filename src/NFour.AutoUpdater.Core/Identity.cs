namespace NFour.AutoUpdater.Core;


public enum HashAlgorithmId
{
    Sha256 = 1,
    Sha512 = 2,
    Md5 = 3,
    Blake3 = 4
}

public readonly struct ContentHash : IEquatable<ContentHash>, IComparable<ContentHash>
{
    private readonly byte[]? _bytes;

    public ContentHash(HashAlgorithmId algorithm, ReadOnlySpan<byte> value)
    {
        Algorithm = algorithm;
        _bytes = value.ToArray();
        Validate(algorithm, _bytes);
    }

    public ContentHash(HashAlgorithmId algorithm, ReadOnlyMemory<byte> value)
        : this(algorithm, value.Span) { }

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

    public bool IsValid => _bytes is not null && IsValidLength(Algorithm, _bytes.Length);

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

    public static async ValueTask<ContentHash> ComputeAsync(Stream source, HashAlgorithmId algorithm = HashAlgorithmId.Sha256, CancellationToken cancellationToken = default)
    {
        using var hash = algorithm switch
        {
            HashAlgorithmId.Sha256 => IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
            HashAlgorithmId.Sha512 => IncrementalHash.CreateHash(HashAlgorithmName.SHA512),
            HashAlgorithmId.Md5 => IncrementalHash.CreateHash(HashAlgorithmName.MD5),
            _ => throw new NotSupportedException($"Hash algorithm {algorithm} is not available in the base implementation.")
        };
        var buffer = new byte[128 * 1024];
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
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
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

    public static ContentHash Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
            throw new FormatException("A content hash must use the form algorithm:lowercase-hex.");
        var algorithm = value[..separator] switch
        {
            "sha256" => HashAlgorithmId.Sha256,
            "sha512" => HashAlgorithmId.Sha512,
            "md5" => HashAlgorithmId.Md5,
            "blake3" => HashAlgorithmId.Blake3,
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

    public override string ToString() => IsValid
        ? $"{AlgorithmName(Algorithm)}:{Convert.ToHexString(Span).ToLowerInvariant()}"
        : throw new InvalidOperationException("The default ContentHash is invalid.");

    public bool Equals(ContentHash other) => Algorithm == other.Algorithm && Span.SequenceEqual(other.Span);
    public override bool Equals(object? obj) => obj is ContentHash other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Algorithm);
        foreach (var b in Span) hash.Add(b);
        return hash.ToHashCode();
    }

    public int CompareTo(ContentHash other)
    {
        var algorithm = Algorithm.CompareTo(other.Algorithm);
        return algorithm != 0 ? algorithm : Span.SequenceCompareTo(other.Span);
    }

    public static bool operator ==(ContentHash left, ContentHash right) => left.Equals(right);
    public static bool operator !=(ContentHash left, ContentHash right) => !left.Equals(right);

    private static string AlgorithmName(HashAlgorithmId algorithm) => algorithm switch
    {
        HashAlgorithmId.Sha256 => "sha256",
        HashAlgorithmId.Sha512 => "sha512",
        HashAlgorithmId.Md5 => "md5",
        HashAlgorithmId.Blake3 => "blake3",
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
    };

    private static void Validate(HashAlgorithmId algorithm, byte[] value)
    {
        if (!IsValidLength(algorithm, value.Length))
            throw new ArgumentException($"{algorithm} digest has an invalid length of {value.Length} bytes.", nameof(value));
    }

    private static bool IsValidLength(HashAlgorithmId algorithm, int length) => algorithm switch
    {
        HashAlgorithmId.Sha256 or HashAlgorithmId.Blake3 => length == 32,
        HashAlgorithmId.Md5 => length == 16,
        HashAlgorithmId.Sha512 => length == 64,
        _ => false
    };
}

/// <param name="Size">
/// The manifest-declared length of the blob.  The downloader needs this to bound a transfer:
/// without it, a mirror can stream indefinitely and the mismatch is only discovered once the
/// stream ends, by which point the disk is already full.
/// </param>
public readonly record struct BlobLocator(ContentHash Content, long Size)
{
    public override string ToString() => Content.ToString();
}

public readonly record struct PackageId
{
    private static readonly System.Text.RegularExpressions.Regex Grammar = new("^[a-z0-9](?:[a-z0-9]|[.-][a-z0-9])*$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    public PackageId(string value)
    {
        if (!TryCreate(value, out var id)) throw new ArgumentException("Invalid package identifier.", nameof(value));
        Value = id.Value;
    }
    public string Value { get; }
    public static bool TryCreate(string? value, out PackageId id)
    {
        if (!string.IsNullOrEmpty(value) && value.Length <= 128 && Grammar.IsMatch(value) && !value.Contains("..", StringComparison.Ordinal) && !value.Contains("--", StringComparison.Ordinal))
        {
            id = new PackageId(value, true);
            return true;
        }
        id = default;
        return false;
    }
    private PackageId(string value, bool _) => Value = value;
    public override string ToString() => Value;
}

public readonly record struct PackageVersion : IComparable<PackageVersion>
{
    private static readonly System.Text.RegularExpressions.Regex Grammar = new("^[a-z0-9](?:[a-z0-9]|[.-][a-z0-9])*$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    public PackageVersion(string label, long sequence)
    {
        // Null-checked before Length: a null label would otherwise surface as a
        // NullReferenceException instead of the argument error callers expect.
        ArgumentNullException.ThrowIfNull(label);
        if (sequence < 0 || label.Length is 0 or > 64 || !Grammar.IsMatch(label) || label.Contains("..", StringComparison.Ordinal) || label.Contains("--", StringComparison.Ordinal))
            throw new ArgumentException("Invalid package version.", nameof(label));
        Label = label;
        Sequence = sequence;
    }
    public string Label { get; }
    public long Sequence { get; }
    public int CompareTo(PackageVersion other) => Sequence.CompareTo(other.Sequence);
    public override string ToString() => Label;
}

public readonly record struct PackageRef(PackageId Id, PackageVersion Version);
