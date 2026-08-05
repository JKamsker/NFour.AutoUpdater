namespace FourSaas.AutoUpdater.Publishing;

public sealed class PublishGate
{
    public async ValueTask<ImmutableArray<Diagnostic>> CheckAsync(ReleaseLock release, IReadOnlyDictionary<PackageId, PackageManifest> manifests, IFileSetComposer? composer = null, CancellationToken cancellationToken = default)
        => await CheckCoreAsync(release, manifests, composer, null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ImmutableArray<Diagnostic>> CheckAsync(ReleaseLock release, IReadOnlyDictionary<PackageId, PackageManifest> manifests, IFileSetComposer composer, IPackageRepository repository, CancellationToken cancellationToken = default)
        => await CheckCoreAsync(release, manifests, composer, repository, cancellationToken).ConfigureAwait(false);

    private async ValueTask<ImmutableArray<Diagnostic>> CheckCoreAsync(ReleaseLock release, IReadOnlyDictionary<PackageId, PackageManifest> manifests, IFileSetComposer? composer, IPackageRepository? repository, CancellationToken cancellationToken)
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
        foreach (var requirement in release.Requirements)
        {
            if (!manifests.TryGetValue(requirement.Package, out var dependent)) continue;
            foreach (var dependency in dependent.Requires)
            {
                var dependencyRequirements = release.Requirements.Where(x => x.Package == dependency.Id).ToArray();
                if (dependencyRequirements.Length == 0 || !dependencyRequirements.Any(candidate => Implies(requirement.When, candidate.When, axes)))
                    diagnostics.Add(new("PKG007", DiagnosticSeverity.Error, $"Package '{requirement.Package}' requires '{dependency.Id}', but the dependency is not selectable wherever the dependent is.", requirement.Package.Value));
                var pin = release.Packages.FirstOrDefault(x => x.Id == dependency.Id);
                if (pin is null || dependency.MinSequence is { } minimum && pin.Sequence < minimum || dependency.MaxSequence is { } maximum && pin.Sequence > maximum)
                    diagnostics.Add(new("PKG007", DiagnosticSeverity.Error, $"Package '{requirement.Package}' requires an unavailable sequence of '{dependency.Id}'.", requirement.Package.Value));
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
            var selections = SelectionEnumerator.EstimatedCount(release.Axes, 4097) > 4096
                ? SelectionEnumerator.EnumeratePairwise(release.Axes, 8192)
                : SelectionEnumerator.Enumerate(release.Axes, int.MaxValue);
            foreach (var selection in selections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolved = new VariantResolver().Resolve(release, selection);
                if (resolved.Diagnostics.Any(static x => x.IsError)) { diagnostics.AddRange(resolved.Diagnostics); continue; }
                if (repository is not null)
                {
                    var composed = await composer!.ComposeAsync(repository, resolved, cancellationToken).ConfigureAwait(false);
                    diagnostics.AddRange(composed.Diagnostics);
                }
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

    private static bool Implies(AxisPredicate dependent, AxisPredicate dependency, IReadOnlyDictionary<string, AxisDefinition> axes)
    {
        foreach (var (axis, dependencyValues) in dependency.Constraints)
        {
            if (!dependent.Constraints.TryGetValue(axis, out var dependentValues)) return false;
            if (!axes.ContainsKey(axis) || !dependentValues.IsSubsetOf(dependencyValues)) return false;
        }
        return true;
    }
}

public static class SelectionEnumerator
{
    public static long EstimatedCount(ImmutableArray<AxisDefinition> axes, long cap = long.MaxValue)
    {
        var total = 1L;
        foreach (var axis in axes)
        {
            var choices = axis.Cardinality == AxisCardinality.One
                ? axis.Values.Length
                : axis.Values.Length < 20
                    ? (1L << axis.Values.Length) - 1
                    : checked((long)axis.Values.Length + ((long)axis.Values.Length * (axis.Values.Length - 1) / 2) + 1);
            if (choices == 0) return 0;
            if (total > cap / choices) return cap + 1;
            total *= choices;
        }
        return total;
    }

    /// Deterministic covering array used once exhaustive enumeration exceeds the
    /// report budget. Every value is paired with every value of every other axis;
    /// Many axes additionally include pair and full-set choices so discriminator
    /// changes are represented in the sample.
    public static ImmutableArray<VariantSelection> EnumeratePairwise(ImmutableArray<AxisDefinition> axes, int maximum = 4096)
    {
        var choices = axes.ToDictionary(x => x.Name, PairwiseChoices, StringComparer.Ordinal);
        var results = new List<VariantSelection>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var baseline = axes.ToDictionary(x => x.Name, x => choices[x.Name][0], StringComparer.Ordinal);
        Add(baseline);
        for (var i = 0; i < axes.Length && results.Count < maximum; i++)
        {
            for (var j = i + 1; j < axes.Length && results.Count < maximum; j++)
                foreach (var left in choices[axes[i].Name])
                    foreach (var right in choices[axes[j].Name])
                    {
                        if (results.Count >= maximum) break;
                        var selection = new Dictionary<string, ImmutableSortedSet<string>>(baseline, StringComparer.Ordinal)
                        {
                            [axes[i].Name] = left,
                            [axes[j].Name] = right
                        };
                        Add(selection);
                    }
        }
        foreach (var axis in axes)
            foreach (var choice in choices[axis.Name])
            {
                if (results.Count >= maximum) break;
                var selection = new Dictionary<string, ImmutableSortedSet<string>>(baseline, StringComparer.Ordinal) { [axis.Name] = choice };
                Add(selection);
            }
        return results.ToImmutableArray();

        void Add(IReadOnlyDictionary<string, ImmutableSortedSet<string>> values)
        {
            if (results.Count >= maximum) return;
            var selection = new VariantSelection { Axes = values.ToImmutableSortedDictionary(StringComparer.Ordinal) };
            if (seen.Add(selection.ToCanonicalString())) results.Add(selection);
        }

        static ImmutableArray<ImmutableSortedSet<string>> PairwiseChoices(AxisDefinition axis)
        {
            var values = axis.Values.Select(x => x.Id).ToArray();
            var result = new List<ImmutableSortedSet<string>>();
            foreach (var value in values) result.Add(ImmutableSortedSet.Create(StringComparer.Ordinal, value));
            if (axis.Cardinality == AxisCardinality.Many)
            {
                for (var i = 0; i < values.Length; i++)
                    for (var j = i + 1; j < values.Length; j++) result.Add(ImmutableSortedSet.Create(StringComparer.Ordinal, values[i], values[j]));
                if (values.Length > 1) result.Add(values.ToImmutableSortedSet(StringComparer.Ordinal));
            }
            return result.ToImmutableArray();
        }
    }

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
