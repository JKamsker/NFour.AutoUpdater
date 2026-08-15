namespace NFour.AutoUpdater.Core;

/// <summary>Identifies the impact of a validation diagnostic.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Provides informational context.</summary>
    Info,
    /// <summary>Identifies a condition that may require attention.</summary>
    Warning,
    /// <summary>Identifies a condition that makes the result invalid.</summary>
    Error
}

/// <summary>Describes a stable, machine-readable validation result.</summary>
/// <param name="Code">The stable diagnostic code.</param>
/// <param name="Severity">The diagnostic impact.</param>
/// <param name="Message">The user-facing explanation.</param>
/// <param name="Subject">The optional document or field associated with the diagnostic.</param>
public sealed record Diagnostic(string Code, DiagnosticSeverity Severity, string Message, string? Subject = null)
{
    /// <summary>Gets whether the diagnostic invalidates the result.</summary>
    public bool IsError => Severity == DiagnosticSeverity.Error;
    /// <summary>Formats the diagnostic for human-readable output.</summary>
    /// <returns>The code, severity, optional subject, and message.</returns>
    public override string ToString() => $"{Code} {Severity} {(Subject is null ? string.Empty : Subject + ": ")}{Message}";
}

/// <summary>Contains an immutable collection of validation diagnostics.</summary>
/// <param name="Items">The contained diagnostics.</param>
public sealed record DiagnosticBag(ImmutableArray<Diagnostic> Items)
{
    /// <summary>Gets an empty diagnostic bag.</summary>
    public static DiagnosticBag Empty { get; } = new([]);
    /// <summary>Gets whether the bag contains at least one error.</summary>
    public bool HasErrors => Items.Any(static x => x.IsError);
    /// <summary>Returns a copy containing one additional diagnostic.</summary>
    /// <param name="diagnostic">The diagnostic to append.</param>
    /// <returns>The updated immutable bag.</returns>
    public DiagnosticBag Add(Diagnostic diagnostic) => this with { Items = Items.Add(diagnostic) };
    /// <summary>Returns a copy containing additional diagnostics.</summary>
    /// <param name="diagnostics">The diagnostics to append.</param>
    /// <returns>The updated immutable bag.</returns>
    public DiagnosticBag AddRange(IEnumerable<Diagnostic> diagnostics) => this with { Items = Items.AddRange(diagnostics) };
}

/// <summary>Represents a document or domain validation failure.</summary>
/// <param name="message">The error message.</param>
public sealed class AutoUpdaterValidationException(string message) : Exception(message);

/// <summary>Validates identifiers used by repository and release documents.</summary>
public static class Identifier
{
    // A segment is an alphanumeric followed by zero or more alphanumerics or
    // separator+alphanumeric pairs. This rejects mixed separators such as
    // "a.-b", as required by the repository grammar.
    private static readonly System.Text.RegularExpressions.Regex Grammar = new("^[a-z0-9](?:[a-z0-9]|[.-][a-z0-9])*$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Determines whether an identifier follows the portable segment grammar.</summary>
    /// <param name="value">The identifier to validate.</param>
    /// <param name="maxLength">The maximum permitted character count.</param>
    /// <returns><see langword="true"/> when the identifier is valid.</returns>
    public static bool IsValid(string? value, int maxLength = 128) =>
        !string.IsNullOrEmpty(value) && value.Length <= maxLength && Grammar.IsMatch(value) &&
        !value.Contains("..", StringComparison.Ordinal) && !value.Contains("--", StringComparison.Ordinal);

    /// <summary>Validates a named identifier using its domain-specific length limit.</summary>
    /// <param name="value">The identifier to validate.</param>
    /// <param name="name">The identifier domain name.</param>
    /// <param name="error">Receives a validation error when the identifier is invalid.</param>
    /// <returns><see langword="true"/> when the identifier is valid.</returns>
    public static bool IsValid(string? value, string name, out string? error)
    {
        if (IsValid(value, name is "axis" or "value" ? 32 : name is "channel" ? 64 : 128))
        {
            error = null;
            return true;
        }
        error = $"{name} must be lowercase ASCII segment grammar and contain no consecutive dots or hyphens.";
        return false;
    }
}
