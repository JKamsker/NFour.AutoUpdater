namespace FourSaas.AutoUpdater.Core;

public enum DiagnosticSeverity { Info, Warning, Error }

public sealed record Diagnostic(string Code, DiagnosticSeverity Severity, string Message, string? Subject = null)
{
    public bool IsError => Severity == DiagnosticSeverity.Error;
    public override string ToString() => $"{Code} {Severity} {(Subject is null ? string.Empty : Subject + ": ")}{Message}";
}

public sealed record DiagnosticBag(ImmutableArray<Diagnostic> Items)
{
    public static DiagnosticBag Empty { get; } = new([]);
    public bool HasErrors => Items.Any(static x => x.IsError);
    public DiagnosticBag Add(Diagnostic diagnostic) => this with { Items = Items.Add(diagnostic) };
    public DiagnosticBag AddRange(IEnumerable<Diagnostic> diagnostics) => this with { Items = Items.AddRange(diagnostics) };
}

public sealed class AutoUpdaterValidationException(string message) : Exception(message);

public static class Identifier
{
    // A segment is an alphanumeric followed by zero or more alphanumerics or
    // separator+alphanumeric pairs. This rejects mixed separators such as
    // "a.-b", as required by the repository grammar.
    private static readonly System.Text.RegularExpressions.Regex Grammar = new("^[a-z0-9](?:[a-z0-9]|[.-][a-z0-9])*$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool IsValid(string? value, int maxLength = 128) =>
        !string.IsNullOrEmpty(value) && value.Length <= maxLength && Grammar.IsMatch(value) &&
        !value.Contains("..", StringComparison.Ordinal) && !value.Contains("--", StringComparison.Ordinal);

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
