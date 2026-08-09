using NFour.AutoUpdater.Core;

namespace NFour.AutoUpdater.Cli;

/// <summary>Parses repeated variant-axis selections from CLI option values.</summary>
public static class SelectionParser
{
    private const char SelectionSeparator = '=';
    private const string AxisIdentifierKind = "axis";
    private const string ValueIdentifierKind = "value";

    /// <summary>Parses <c>axis=value</c> selections into a normalized selection.</summary>
    /// <param name="values">Selection strings supplied by the caller.</param>
    /// <returns>The combined selection, with repeated axis values merged.</returns>
    /// <exception cref="FormatException">A selection or identifier is malformed.</exception>
    public static VariantSelection Parse(IEnumerable<string> values)
    {
        var builder = ImmutableSortedDictionary.CreateBuilder<string, ImmutableSortedSet<string>>(StringComparer.Ordinal);
        foreach (var item in values)
        {
            var separator = item.IndexOf(SelectionSeparator);
            if (separator <= 0 || separator == item.Length - 1)
                throw new FormatException("--select must use axis=value.");

            var axis = item[..separator];
            var value = item[(separator + 1)..];
            if (!Identifier.IsValid(axis, AxisIdentifierKind, out var error)
                || !Identifier.IsValid(value, ValueIdentifierKind, out error))
                throw new FormatException(error);
            builder[axis] = builder.TryGetValue(axis, out var existing)
                ? existing.Add(value)
                : ImmutableSortedSet.Create(StringComparer.Ordinal, value);
        }
        return new VariantSelection { Axes = builder.ToImmutable() };
    }
}
