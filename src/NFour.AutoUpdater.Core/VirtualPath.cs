namespace NFour.AutoUpdater.Core;

/// <summary>Represents a normalized, portable path relative to an installation root.</summary>
public readonly record struct VirtualPath : IComparable<VirtualPath>
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "nul", "aux", "prn", "clock$",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
    };

    /// <summary>Initializes a validated portable path.</summary>
    /// <param name="raw">The relative path to normalize and validate.</param>
    public VirtualPath(string raw)
    {
        if (!TryCreate(raw, out var path, out var error)) throw new ArgumentException(error, nameof(raw));
        Value = path.Value;
    }

    /// <summary>Gets the normalized slash-separated path.</summary>
    public string Value { get; }
    /// <summary>Gets the comparison key used to detect portable path collisions.</summary>
    public string FoldedKey => Fold(Value);

    /// <summary>Creates a validated portable path.</summary>
    /// <param name="raw">The relative path to normalize and validate.</param>
    /// <returns>The validated path.</returns>
    public static VirtualPath Create(string raw) => new(raw);

    /// <summary>Attempts to normalize and validate a portable path.</summary>
    /// <param name="raw">The relative path to validate.</param>
    /// <param name="path">Receives the validated path.</param>
    /// <param name="error">Receives the validation error when parsing fails.</param>
    /// <returns><see langword="true"/> when the path is valid.</returns>
    public static bool TryCreate(string? raw, out VirtualPath path, out string? error)
    {
        path = default;
        error = null;
        if (string.IsNullOrWhiteSpace(raw)) return Fail(out error, "Path is empty.");
        var normalized = raw.Normalize(NormalizationForm.FormC).Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':') || normalized.Contains("//", StringComparison.Ordinal) || normalized.StartsWith("//", StringComparison.Ordinal))
            return Fail(out error, "Path must be relative, slash-separated, and contain no drive or UNC prefix.");
        if (normalized.Split('/').Any(static x => x is "" or "." or "..")) return Fail(out error, "Path contains an empty, '.', or '..' component.");
        if (normalized.Any(c => c is '<' or '>' or '"' or '|' or '?' or '*' || char.IsControl(c))) return Fail(out error, "Path contains a Windows-illegal character.");
        if (normalized.Length > 260) return Fail(out error, "Path exceeds the portable 260-character budget.");
        foreach (var component in normalized.Split('/'))
        {
            if (component.EndsWith(' ') || component.EndsWith('.')) return Fail(out error, "A path component may not end in a dot or space.");
            var stem = component.Split('.')[0];
            if (ReservedNames.Contains(stem)) return Fail(out error, $"'{component}' is a Windows reserved device name.");
            if (component.Length > 255) return Fail(out error, "A path component exceeds 255 characters.");
        }
        path = new VirtualPath(normalized, true);
        return true;

        static bool Fail(out string? target, string message) { target = message; return false; }
    }

    private VirtualPath(string value, bool _) => Value = value;
    /// <inheritdoc />
    public int CompareTo(VirtualPath other) => StringComparer.Ordinal.Compare(Value, other.Value);
    /// <summary>Returns the normalized path.</summary>
    /// <returns>The slash-separated path.</returns>
    public override string ToString() => Value;

    /// <summary>Creates the platform-neutral collision-detection key for a path.</summary>
    /// <param name="value">The normalized path.</param>
    /// <returns>The case-folded path key.</returns>
    public static string Fold(string value)
    {
        var components = value.Normalize(NormalizationForm.FormC).Split('/');
        return string.Join('/', components.Select(static component => component.TrimEnd(' ', '.').ToUpperInvariant()));
    }
}
