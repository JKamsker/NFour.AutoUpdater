namespace NFour.AutoUpdater.Core;

/// <summary>Represents a validated package identifier.</summary>
public readonly record struct PackageId
{
    private const int MaximumLength = 128;
    private static readonly System.Text.RegularExpressions.Regex Grammar = new("^[a-z0-9](?:[a-z0-9]|[.-][a-z0-9])*$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Initializes a validated package identifier.</summary>
    /// <param name="value">The identifier text.</param>
    public PackageId(string value)
    {
        if (!TryCreate(value, out var id)) throw new ArgumentException("Invalid package identifier.", nameof(value));
        Value = id.Value;
    }

    /// <summary>Gets the canonical package identifier.</summary>
    public string Value { get; }

    /// <summary>Attempts to create a package identifier.</summary>
    /// <param name="value">The identifier text.</param>
    /// <param name="id">Receives the validated identifier.</param>
    /// <returns><see langword="true"/> when the identifier is valid.</returns>
    public static bool TryCreate(string? value, out PackageId id)
    {
        if (!string.IsNullOrEmpty(value) && value.Length <= MaximumLength && Grammar.IsMatch(value) && !value.Contains("..", StringComparison.Ordinal) && !value.Contains("--", StringComparison.Ordinal))
        {
            id = new PackageId(value, true);
            return true;
        }
        id = default;
        return false;
    }

    private PackageId(string value, bool _) => Value = value;

    /// <summary>Returns the canonical package identifier.</summary>
    /// <returns>The identifier text.</returns>
    public override string ToString() => Value;
}

/// <summary>Represents a labelled package version with a monotonic sequence.</summary>
public readonly record struct PackageVersion : IComparable<PackageVersion>
{
    private const int MaximumLabelLength = 64;
    private static readonly System.Text.RegularExpressions.Regex Grammar = new("^[a-z0-9](?:[a-z0-9]|[.-][a-z0-9])*$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Initializes a validated package version.</summary>
    /// <param name="label">The user-facing version label.</param>
    /// <param name="sequence">The non-negative monotonic sequence.</param>
    public PackageVersion(string label, long sequence)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (sequence < 0 || label.Length is 0 or > MaximumLabelLength || !Grammar.IsMatch(label) || label.Contains("..", StringComparison.Ordinal) || label.Contains("--", StringComparison.Ordinal))
            throw new ArgumentException("Invalid package version.", nameof(label));
        Label = label;
        Sequence = sequence;
    }

    /// <summary>Gets the version label.</summary>
    public string Label { get; }
    /// <summary>Gets the monotonic version sequence.</summary>
    public long Sequence { get; }
    /// <inheritdoc />
    public int CompareTo(PackageVersion other) => Sequence.CompareTo(other.Sequence);
    /// <summary>Returns the version label.</summary>
    /// <returns>The label text.</returns>
    public override string ToString() => Label;
}

/// <summary>Identifies one immutable package version.</summary>
/// <param name="Id">The package identifier.</param>
/// <param name="Version">The package version.</param>
public readonly record struct PackageRef(PackageId Id, PackageVersion Version);
