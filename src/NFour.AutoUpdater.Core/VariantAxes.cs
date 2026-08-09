namespace NFour.AutoUpdater.Core;

/// <summary>Defines how many values may be selected for an axis.</summary>
public enum AxisCardinality
{
    /// <summary>Exactly one value may be selected.</summary>
    One,
    /// <summary>Multiple values may be selected.</summary>
    Many
}
/// <summary>Identifies the role of a package.</summary>
public enum PackageKind
{
    /// <summary>The package contributes installable content.</summary>
    Content,
    /// <summary>The package groups or constrains other packages without content.</summary>
    Meta
}
/// <summary>Describes the publication lifecycle of a package.</summary>
public enum PackageState
{
    /// <summary>The package is still being authored.</summary>
    Draft,
    /// <summary>The package is available for resolution.</summary>
    Published,
    /// <summary>The package remains addressable but must not be newly selected.</summary>
    Yanked
}
/// <summary>Describes the publication lifecycle of a release.</summary>
public enum ReleaseState
{
    /// <summary>The release is still being authored.</summary>
    Draft,
    /// <summary>The release is available for installation.</summary>
    Published,
    /// <summary>The release remains addressable but must not be newly installed.</summary>
    Yanked
}
/// <summary>Defines how an installed file is materialized and maintained.</summary>
public enum FileInstallPolicy
{
    /// <summary>Replace the destination with the package content.</summary>
    Replace,
    /// <summary>Preserve an existing destination file.</summary>
    Preserve,
    /// <summary>Replace the destination and mark it executable where supported.</summary>
    Executable
}
/// <summary>Identifies the kind of file-table entry.</summary>
public enum FileEntryKind
{
    /// <summary>A regular file.</summary>
    File,
    /// <summary>A directory.</summary>
    Directory
}

/// <summary>Describes one selectable value of a variant axis.</summary>
public sealed record AxisValue
{
    /// <summary>Gets the stable identifier used in manifests and selections.</summary>
    public required string Id { get; init; }
    /// <summary>Gets the optional user-facing label.</summary>
    public string? Display { get; init; }
}

/// <summary>Defines a variant axis, its precedence, and its allowed values.</summary>
public sealed record AxisDefinition
{
    /// <summary>Gets the stable axis name.</summary>
    public required string Name { get; init; }
    /// <summary>Gets the precedence rank used while composing package layers.</summary>
    public required int Rank { get; init; }
    /// <summary>Gets the live values allowed for the axis.</summary>
    public required ImmutableArray<AxisValue> Values { get; init; }
    /// <summary>Gets the default value, if one is defined.</summary>
    public string? Default { get; init; }
    /// <summary>Gets the number of values that may be selected.</summary>
    public required AxisCardinality Cardinality { get; init; }
    /// <summary>Gets whether the selection must contain this axis.</summary>
    public required bool Required { get; init; }
    /// <summary>Gets the optional user-facing axis label.</summary>
    public string? DisplayName { get; init; }
    /// <summary>Gets mappings from retired value identifiers to their replacements.</summary>
    public ImmutableDictionary<string, string> Retired { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>Finds the zero-based declaration index of a live value.</summary>
    /// <param name="value">The value identifier to locate.</param>
    /// <returns>The value index, or <c>-1</c> when no live value matches.</returns>
    public int IndexOf(string value)
    {
        for (var i = 0; i < Values.Length; i++) if (StringComparer.Ordinal.Equals(Values[i].Id, value)) return i;
        return -1;
    }

    /// <summary>Resolves a retired value through its replacement chain.</summary>
    /// <param name="value">The live or retired value identifier.</param>
    /// <param name="resolved">Receives the final live identifier.</param>
    /// <param name="retired">Receives whether at least one retirement mapping was followed.</param>
    /// <param name="error">Receives a validation error when resolution fails.</param>
    /// <returns><see langword="true"/> when the value resolves to a live value.</returns>
    public bool TryResolveRetired(string value, out string resolved, out bool retired, out string? error)
    {
        resolved = value;
        retired = false;
        error = null;
        for (var i = 0; i < 8 && Retired.TryGetValue(resolved, out var replacement); i++)
        {
            retired = true;
            resolved = replacement;
        }
        if (Retired.ContainsKey(resolved))
        {
            error = "Retirement mapping exceeds the eight-hop limit or contains a cycle.";
            return false;
        }
        if (IndexOf(resolved) < 0)
        {
            error = $"Retirement replacement '{resolved}' is not a live axis value.";
            return false;
        }
        return true;
    }

    /// <summary>Returns a copy whose retirement mappings point directly to live values.</summary>
    /// <returns>The normalized axis definition.</returns>
    public AxisDefinition NormalizeRetiredMappings()
    {
        var normalized = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var oldValue in Retired.Keys.OrderBy(static value => value, StringComparer.Ordinal))
        {
            if (!TryResolveRetired(oldValue, out var replacement, out _, out var error))
                throw new FormatException($"Axis '{Name}' has an invalid retirement mapping for '{oldValue}': {error}");
            normalized[oldValue] = replacement;
        }
        return this with { Retired = normalized.ToImmutable() };
    }
}

/// <summary>Contains the selected value set for each variant axis.</summary>
public sealed record VariantSelection
{
    /// <summary>Gets the selected values keyed by axis name.</summary>
    public required ImmutableSortedDictionary<string, ImmutableSortedSet<string>> Axes { get; init; }
    /// <summary>Gets the selected values for an axis, or an empty set when the axis is absent.</summary>
    /// <param name="axis">The axis name.</param>
    public ImmutableSortedSet<string> this[string axis] => Axes.TryGetValue(axis, out var values) ? values : ImmutableSortedSet<string>.Empty;
    /// <summary>Determines whether an axis contains a selected value.</summary>
    /// <param name="axis">The axis name.</param>
    /// <param name="value">The value identifier.</param>
    /// <returns><see langword="true"/> when the value is selected.</returns>
    public bool Has(string axis, string value) => this[axis].Contains(value);
    /// <summary>Formats the selection in deterministic axis and value order.</summary>
    /// <returns>The canonical selection string.</returns>
    public string ToCanonicalString() => string.Join(';', Axes
        .Where(x => !x.Value.IsEmpty)
        .OrderBy(x => x.Key, StringComparer.Ordinal)
        .Select(x => $"{x.Key}={string.Join(',', x.Value.OrderBy(static v => v, StringComparer.Ordinal))}"));
    /// <summary>Gets the content hash of the canonical selection string.</summary>
    public ContentHash SelectionId => ContentHash.Compute(Encoding.UTF8.GetBytes(ToCanonicalString()));
}

/// <summary>Constrains a package requirement to selected axis values.</summary>
public sealed record AxisPredicate
{
    /// <summary>Gets the accepted values keyed by axis name.</summary>
    public required ImmutableSortedDictionary<string, ImmutableSortedSet<string>> Constraints { get; init; }
    /// <summary>Gets a predicate that matches every selection.</summary>
    public static AxisPredicate Always { get; } = new() { Constraints = ImmutableSortedDictionary<string, ImmutableSortedSet<string>>.Empty };
    /// <summary>Gets whether the predicate has no constraints.</summary>
    public bool IsAlways => Constraints.IsEmpty;
    /// <summary>Determines whether a selection satisfies every constraint.</summary>
    /// <param name="selection">The selection to evaluate.</param>
    /// <returns><see langword="true"/> when all constraints match.</returns>
    public bool Matches(VariantSelection selection) => Constraints.All(x => selection[x.Key].Overlaps(x.Value));

    /// <summary>Determines whether two predicates can match the same valid selection.</summary>
    /// <param name="other">The other predicate.</param>
    /// <param name="axes">The declared axes.</param>
    /// <returns><see langword="true"/> when the predicates can coexist.</returns>
    public bool CanCoexistWith(AxisPredicate other, IReadOnlyDictionary<string, AxisDefinition> axes)
    {
        foreach (var (axisName, left) in Constraints)
        {
            if (!other.Constraints.TryGetValue(axisName, out var right)) continue;
            if (!axes.TryGetValue(axisName, out var definition)) return false;
            if (definition.Cardinality == AxisCardinality.One && !left.Overlaps(right)) return false;
        }
        return true;
    }
}

