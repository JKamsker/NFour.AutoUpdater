namespace FourSaas.AutoUpdater.Publishing;

public sealed class PublishGate
{
    public async ValueTask<ImmutableArray<Diagnostic>> CheckAsync(ReleaseLock release, IReadOnlyDictionary<PackageId, PackageManifest> manifests, IFileSetComposer? composer = null, CancellationToken cancellationToken = default)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRelease(release, manifests, diagnostics);
        var axes = release.Axes.ToDictionary(x => x.Name, StringComparer.Ordinal);
        foreach (var requirement in release.Requirements)
        {
            foreach (var axis in requirement.When.Constraints)
            {
                if (!axes.TryGetValue(axis.Key, out var definition)) { diagnostics.Add(new("PKG003", DiagnosticSeverity.Error, $"Requirement references undeclared axis '{axis.Key}'.", requirement.Package.Value)); continue; }
                foreach (var value in axis.Value) if (definition.IndexOf(value) < 0 && !definition.Retired.ContainsKey(value)) diagnostics.Add(new("PKG003", DiagnosticSeverity.Error, $"Requirement references unknown value '{value}'.", requirement.Package.Value));
            }
        }
        for (var i = 0; i < release.Requirements.Length; i++)
            for (var j = i + 1; j < release.Requirements.Length; j++)
            {
                var left = release.Requirements[i]; var right = release.Requirements[j];
                if (!left.When.CanCoexistWith(right.When, axes)) continue;
                if (!manifests.TryGetValue(left.Package, out var leftManifest) || !manifests.TryGetValue(right.Package, out var rightManifest)) continue;
                if (leftManifest.PathPrefixes.Length > 0 && rightManifest.PathPrefixes.Length > 0 && !leftManifest.PathPrefixes.Any(a => rightManifest.PathPrefixes.Any(b => a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)))) continue;
                if (leftManifest.Conflicts.Contains(right.Package) || rightManifest.Conflicts.Contains(left.Package)) diagnostics.Add(new("PKG006", DiagnosticSeverity.Error, $"Conflicting packages '{left.Package}' and '{right.Package}' can be selected together."));
                if (left.LayerOverride is not null) diagnostics.Add(new("PKG010", DiagnosticSeverity.Warning, "An explicit layer override was used.", left.Package.Value));
                if (right.LayerOverride is not null) diagnostics.Add(new("PKG010", DiagnosticSeverity.Warning, "An explicit layer override was used.", right.Package.Value));
            }
        if (composer is not null && !diagnostics.Any(static x => x.IsError))
        {
            var selections = SelectionEnumerator.Enumerate(release.Axes, 4096);
            foreach (var selection in selections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolved = new VariantResolver().Resolve(release, selection);
                if (resolved.Diagnostics.Any(static x => x.IsError)) { diagnostics.AddRange(resolved.Diagnostics); continue; }
            }
        }
        await Task.CompletedTask;
        return diagnostics.ToImmutable();
    }

    private static void ValidateRelease(ReleaseLock release, IReadOnlyDictionary<PackageId, PackageManifest> manifests, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (release.SchemaVersion != 1) diagnostics.Add(new("DOC001", DiagnosticSeverity.Error, $"Unsupported release schemaVersion {release.SchemaVersion}."));
        if (release.Axes.Select(x => x.Rank).Distinct().Count() != release.Axes.Length) diagnostics.Add(new("PKG001", DiagnosticSeverity.Error, "Axis ranks must be unique."));
        var ids = release.Axes.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var axis in release.Axes)
        {
            if (!Identifier.IsValid(axis.Name, "axis", out var error)) diagnostics.Add(new("PKG003", DiagnosticSeverity.Error, error!, axis.Name));
            if (axis.Values.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != axis.Values.Length) diagnostics.Add(new("PKG003", DiagnosticSeverity.Error, "Axis values must be unique.", axis.Name));
            foreach (var value in axis.Values)
                if (!Identifier.IsValid(value.Id, "value", out var valueError)) diagnostics.Add(new("PKG003", DiagnosticSeverity.Error, valueError!, axis.Name));
            foreach (var old in axis.Retired.Keys)
            {
                if (!axis.TryResolveRetired(old, out _, out _, out var retirementError)) diagnostics.Add(new("PKG017", DiagnosticSeverity.Error, retirementError!, axis.Name));
            }
        }
        foreach (var requirement in release.Requirements)
        {
            if (!manifests.ContainsKey(requirement.Package) && !release.Packages.Any(x => x.Id == requirement.Package)) diagnostics.Add(new("PKG005", DiagnosticSeverity.Error, $"Requirement references missing package '{requirement.Package}'."));
            foreach (var overrideId in requirement.Overrides) if (!release.Packages.Any(x => x.Id == overrideId)) diagnostics.Add(new("PKG016", DiagnosticSeverity.Error, $"Override '{overrideId}' is not pinned.", requirement.Package.Value));
        }
        foreach (var pin in release.Packages)
        {
            if (!manifests.TryGetValue(pin.Id, out var manifest))
            {
                diagnostics.Add(new("PKG015", DiagnosticSeverity.Error, $"Pinned manifest '{pin.Id}' is unavailable for digest validation.", pin.Id.Value));
                continue;
            }
            if (ContentHash.Compute(RepositoryJson.SerializeManifest(manifest)) != pin.ManifestDigest)
                diagnostics.Add(new("PKG015", DiagnosticSeverity.Error, $"Pinned manifest '{pin.Id}' does not match manifestDigest.", pin.Id.Value));
            if (pin.Version.Label != manifest.Version.Label || pin.Sequence != (manifest.Sequence == 0 ? manifest.Version.Sequence : manifest.Sequence) || pin.FileCount != manifest.FileCount || pin.InstallSize != manifest.InstallSize || pin.DownloadSize != manifest.DownloadSize)
                diagnostics.Add(new("PKG016", DiagnosticSeverity.Error, $"Pinned metadata for '{pin.Id}' is not denormalised from its manifest.", pin.Id.Value));
            var expectedOverrides = release.Requirements.Where(x => x.Package == pin.Id).SelectMany(x => x.Overrides).Distinct().OrderBy(x => x.Value, StringComparer.Ordinal).ToArray();
            var actualOverrides = pin.Overrides.Distinct().OrderBy(x => x.Value, StringComparer.Ordinal).ToArray();
            if (!expectedOverrides.SequenceEqual(actualOverrides)) diagnostics.Add(new("PKG016", DiagnosticSeverity.Error, $"Pinned overrides for '{pin.Id}' do not match its requirements.", pin.Id.Value));
        }
        foreach (var manifest in manifests.Values)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prefix in manifest.PathPrefixes) if (prefix != prefix.Normalize(NormalizationForm.FormC)) diagnostics.Add(new("PKG012", DiagnosticSeverity.Error, "Path prefix is not NFC.", manifest.Id.Value));
            if (manifest.FileCount < 0 || manifest.InstallSize < 0 || manifest.DownloadSize < 0) diagnostics.Add(new("PKG012", DiagnosticSeverity.Error, "Manifest counts and sizes cannot be negative.", manifest.Id.Value));
        }
    }
}

public static class SelectionEnumerator
{
    public static ImmutableArray<VariantSelection> Enumerate(ImmutableArray<AxisDefinition> axes, int maximum = int.MaxValue)
    {
        var results = new List<VariantSelection>();
        var builder = ImmutableSortedDictionary.CreateBuilder<string, ImmutableSortedSet<string>>(StringComparer.Ordinal);
        Visit(0);
        return results.ToImmutableArray();
        void Visit(int index)
        {
            if (results.Count >= maximum) return;
            if (index == axes.Length) { results.Add(new VariantSelection { Axes = builder.ToImmutable() }); return; }
            var axis = axes[index];
            var choices = axis.Cardinality == AxisCardinality.One ? axis.Values.Select(x => ImmutableSortedSet.Create(StringComparer.Ordinal, x.Id)) : NonEmptySubsets(axis.Values.Select(x => x.Id));
            foreach (var choice in choices) { builder[axis.Name] = choice; Visit(index + 1); if (results.Count >= maximum) break; }
            builder.Remove(axis.Name);
        }
    }
    private static IEnumerable<ImmutableSortedSet<string>> NonEmptySubsets(IEnumerable<string> values)
    {
        var list = values.ToArray();
        if (list.Length < 20)
        {
            var limit = 1L << list.Length;
            for (var mask = 1L; mask < limit; mask++) yield return list.Where((_, i) => (mask & (1L << i)) != 0).ToImmutableSortedSet(StringComparer.Ordinal);
            yield break;
        }

        // Exhaustive subset enumeration is not bounded for large Many axes.
        // The deterministic sample retains every singleton, every pair, and
        // the full set, which gives the gate useful pairwise coverage without
        // integer-shift overflow or unbounded memory.
        foreach (var value in list) yield return ImmutableSortedSet.Create(StringComparer.Ordinal, value);
        for (var i = 0; i < list.Length; i++)
            for (var j = i + 1; j < list.Length; j++)
                yield return ImmutableSortedSet.Create(StringComparer.Ordinal, list[i], list[j]);
        yield return list.ToImmutableSortedSet(StringComparer.Ordinal);
    }
}
