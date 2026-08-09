namespace NFour.AutoUpdater.Publishing;

public sealed class PublishGate
{
    public async ValueTask<ImmutableArray<Diagnostic>> CheckAsync(ReleaseLock release, IReadOnlyDictionary<PackageId, PackageManifest> manifests, IFileSetComposer? composer = null, CancellationToken cancellationToken = default)
        => await CheckCoreAsync(release, manifests, composer, null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ImmutableArray<Diagnostic>> CheckAsync(ReleaseLock release, IReadOnlyDictionary<PackageId, PackageManifest> manifests, IFileSetComposer composer, IPackageRepository repository, CancellationToken cancellationToken = default)
        => await CheckCoreAsync(release, manifests, composer, repository, cancellationToken).ConfigureAwait(false);

    private async ValueTask<ImmutableArray<Diagnostic>> CheckCoreAsync(ReleaseLock release, IReadOnlyDictionary<PackageId, PackageManifest> manifests, IFileSetComposer? composer, IPackageRepository? repository, CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        Dictionary<PackageId, ContentHash>? exactManifestDigests = null;
        if (repository is IExactManifestRepository exactRepository)
        {
            exactManifestDigests = [];
            foreach (var pin in release.Packages)
            {
                var exactBytes = await exactRepository.GetManifestBytesAsync(pin.Id, new PackageVersion(pin.Version.Label, pin.Sequence), cancellationToken).ConfigureAwait(false);
                if (exactBytes is null) diagnostics.Add(new("PKG015", DiagnosticSeverity.Error, $"Pinned manifest '{pin.Id}' is unavailable for digest validation.", pin.Id.Value));
                else exactManifestDigests[pin.Id] = ContentHash.Compute(exactBytes);
            }
        }
        ValidateRelease(release, manifests, diagnostics, exactManifestDigests);

        // Duplicate axis names are reported, not thrown. ToDictionary raises on the second
        // occurrence, so a release declaring the same axis twice aborted the gate with an
        // exception instead of producing the diagnostic the publisher needs to fix it — and
        // every other problem in the release stayed hidden behind it.
        var duplicateAxes = release.Axes.GroupBy(x => x.Name, StringComparer.Ordinal).Where(x => x.Count() > 1).Select(x => x.Key).ToArray();
        foreach (var duplicate in duplicateAxes)
            diagnostics.Add(new("PKG016", DiagnosticSeverity.Error, $"Axis '{duplicate}' is declared more than once.", duplicate));
        foreach (var blank in release.Axes.Where(x => string.IsNullOrWhiteSpace(x.Name)))
            diagnostics.Add(new("PKG016", DiagnosticSeverity.Error, "An axis is declared with an empty name.", blank.Name ?? string.Empty));

        var axes = new Dictionary<string, AxisDefinition>(StringComparer.Ordinal);
        foreach (var axis in release.Axes) axes.TryAdd(axis.Name, axis);
        var mentionedValues = release.Requirements
            .SelectMany(x => x.When.Constraints)
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.SelectMany(y => y.Value).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var axis in release.Axes)
            foreach (var value in axis.Values)
                if (!mentionedValues.TryGetValue(axis.Name, out var values) || !values.Contains(value.Id))
                    diagnostics.Add(new("PKG004", DiagnosticSeverity.Warning, $"Axis value '{value.Id}' is not mentioned by any requirement.", axis.Name));
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
                var leftLayer = DerivedLayer(left, axes);
                var rightLayer = DerivedLayer(right, axes);
                if (left.Package == right.Package && leftLayer != rightLayer)
                    diagnostics.Add(new("PKG008", DiagnosticSeverity.Error, $"Package '{left.Package}' has inconsistent derived layers.", left.Package.Value));
                if (!manifests.TryGetValue(left.Package, out var leftManifest) || !manifests.TryGetValue(right.Package, out var rightManifest)) continue;
                if (leftManifest.Conflicts.Contains(right.Package) || rightManifest.Conflicts.Contains(left.Package)) diagnostics.Add(new("PKG006", DiagnosticSeverity.Error, $"Conflicting packages '{left.Package}' and '{right.Package}' can be selected together."));
                if (!MayOverlap(leftManifest.PathPrefixes, rightManifest.PathPrefixes)) continue;
                foreach (var (leftDiscriminator, rightDiscriminator) in DistinctDiscriminatorOutcomes(left, right, axes))
                {
                    if (leftLayer == rightLayer && leftDiscriminator == rightDiscriminator)
                        diagnostics.Add(new("PKG001", DiagnosticSeverity.Error, $"Packages '{left.Package}' and '{right.Package}' can own an overlapping path at the same layer and discriminator."));
                    else if (leftLayer > rightLayer && !left.Overrides.Contains(right.Package))
                        diagnostics.Add(new("PKG002", DiagnosticSeverity.Error, $"Package '{left.Package}' may shadow '{right.Package}' without declaring an override.", left.Package.Value));
                    else if (rightLayer > leftLayer && !right.Overrides.Contains(left.Package))
                        diagnostics.Add(new("PKG002", DiagnosticSeverity.Error, $"Package '{right.Package}' may shadow '{left.Package}' without declaring an override.", right.Package.Value));
                }
                if (left.LayerOverride is not null) diagnostics.Add(new("PKG010", DiagnosticSeverity.Warning, "An explicit layer override was used.", left.Package.Value));
                if (right.LayerOverride is not null) diagnostics.Add(new("PKG010", DiagnosticSeverity.Warning, "An explicit layer override was used.", right.Package.Value));
            }
        if (composer is not null && !diagnostics.Any(static x => x.IsError))
        {
            ImmutableArray<VariantSelection> selections;
            if (SelectionEnumerator.EstimatedCount(release.Axes, 4097) > 4096)
            {
                selections = SelectionEnumerator.EnumeratePairwise(release.Axes, 8192, out var truncated);
                if (truncated)
                {
                    diagnostics.Add(new("PKG019", DiagnosticSeverity.Error, "The deterministic pairwise coverage sample exceeded its bound and would be incomplete."));
                    return diagnostics.ToImmutable();
                }
            }
            else selections = SelectionEnumerator.Enumerate(release.Axes, int.MaxValue);
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

    private static void ValidateRelease(ReleaseLock release, IReadOnlyDictionary<PackageId, PackageManifest> manifests, ImmutableArray<Diagnostic>.Builder diagnostics, IReadOnlyDictionary<PackageId, ContentHash>? exactManifestDigests = null)
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
                if (!axis.TryResolveRetired(old, out var replacement, out _, out var retirementError))
                    diagnostics.Add(new(retirementError?.Contains("not a live axis value", StringComparison.Ordinal) == true ? "PKG018" : "PKG017", DiagnosticSeverity.Error, retirementError!, axis.Name));
                else if (!string.Equals(axis.Retired[old], replacement, StringComparison.Ordinal))
                    diagnostics.Add(new("PKG017", DiagnosticSeverity.Error, $"Retirement mapping '{old}' must point directly to terminal value '{replacement}'.", axis.Name));
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
            var actualManifestDigest = exactManifestDigests is not null && exactManifestDigests.TryGetValue(pin.Id, out var exactDigest)
                ? exactDigest
                : ContentHash.Compute(RepositoryJson.SerializeManifest(manifest));
            if (actualManifestDigest != pin.ManifestDigest)
                diagnostics.Add(new("PKG015", DiagnosticSeverity.Error, $"Pinned manifest '{pin.Id}' does not match manifestDigest.", pin.Id.Value));
            var expectedRequires = NormalizeDependencies(manifest.Requires);
            var actualRequires = NormalizeDependencies(pin.Requires);
            var expectedConflicts = manifest.Conflicts.Select(x => x.Value).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var actualConflicts = pin.Conflicts.Select(x => x.Value).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (pin.Version.Label != manifest.Version.Label || pin.Sequence != (manifest.Sequence == 0 ? manifest.Version.Sequence : manifest.Sequence) || pin.FileCount != manifest.FileCount || pin.InstallSize != manifest.InstallSize || pin.DownloadSize != manifest.DownloadSize || !expectedRequires.SequenceEqual(actualRequires) || !expectedConflicts.SequenceEqual(actualConflicts))
                diagnostics.Add(new("PKG016", DiagnosticSeverity.Error, $"Pinned metadata for '{pin.Id}' is not denormalised from its manifest.", pin.Id.Value));
            var expectedOverrides = release.Requirements.Where(x => x.Package == pin.Id).SelectMany(x => x.Overrides).Distinct().OrderBy(x => x.Value, StringComparer.Ordinal).ToArray();
            var actualOverrides = pin.Overrides.Distinct().OrderBy(x => x.Value, StringComparer.Ordinal).ToArray();
            if (!expectedOverrides.SequenceEqual(actualOverrides)) diagnostics.Add(new("PKG016", DiagnosticSeverity.Error, $"Pinned overrides for '{pin.Id}' do not match its requirements.", pin.Id.Value));
        }
        foreach (var manifest in manifests.Values)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prefix in manifest.PathPrefixes)
            {
                if (prefix != prefix.Normalize(NormalizationForm.FormC)) diagnostics.Add(new("PKG012", DiagnosticSeverity.Error, "Path prefix is not NFC.", manifest.Id.Value));
                var folded = prefix.ToUpperInvariant();
                if (!seen.Add(folded)) diagnostics.Add(new("PKG009", DiagnosticSeverity.Error, "Package contains case-only path prefixes.", manifest.Id.Value));
            }
            if (manifest.FileCount < 0 || manifest.InstallSize < 0 || manifest.DownloadSize < 0) diagnostics.Add(new("PKG012", DiagnosticSeverity.Error, "Manifest counts and sizes cannot be negative.", manifest.Id.Value));
        }
    }

    private static bool Implies(AxisPredicate dependent, AxisPredicate dependency, IReadOnlyDictionary<string, AxisDefinition> axes)
    {
        foreach (var (axis, dependencyValues) in dependency.Constraints)
        {
            if (!axes.TryGetValue(axis, out var definition)) return false;
            if (!dependent.Constraints.TryGetValue(axis, out var dependentValues))
            {
                if (!definition.Values.Select(x => x.Id).ToImmutableHashSet(StringComparer.Ordinal).SetEquals(dependencyValues)) return false;
                continue;
            }
            if (!dependentValues.IsSubsetOf(dependencyValues)) return false;
        }
        return true;
    }

    private static IEnumerable<(string Id, long? MinSequence, long? MaxSequence)> NormalizeDependencies(IEnumerable<PackageDependency> dependencies) =>
        dependencies
            .OrderBy(x => x.Id.Value, StringComparer.Ordinal)
            .ThenBy(x => x.MinSequence)
            .ThenBy(x => x.MaxSequence)
            .Select(x => (x.Id.Value, x.MinSequence, x.MaxSequence));

    private static bool MayOverlap(ImmutableArray<string> left, ImmutableArray<string> right) =>
        left.IsDefaultOrEmpty || right.IsDefaultOrEmpty || left.Any(a => right.Any(b => a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase)));

    private static int DerivedLayer(PackageRequirement requirement, IReadOnlyDictionary<string, AxisDefinition> axes) =>
        requirement.LayerOverride ?? (requirement.RankAs is { } rankAs && axes.TryGetValue(rankAs, out var ranked) ? ranked.Rank * 1000 : requirement.When.IsAlways ? 0 : requirement.When.Constraints.Keys.Where(axes.ContainsKey).Select(x => axes[x].Rank).DefaultIfEmpty(0).Max() * 1000);

    private static IEnumerable<(int Left, int Right)> DistinctDiscriminatorOutcomes(PackageRequirement left, PackageRequirement right, IReadOnlyDictionary<string, AxisDefinition> axes)
    {
        var leftAxis = left.RankAs is null && !left.When.IsAlways ? left.When.Constraints.Keys.Where(axes.ContainsKey).Select(x => axes[x]).MaxBy(x => x.Rank) : null;
        var rightAxis = right.RankAs is null && !right.When.IsAlways ? right.When.Constraints.Keys.Where(axes.ContainsKey).Select(x => axes[x]).MaxBy(x => x.Rank) : null;
        if (leftAxis is not null && rightAxis is not null && leftAxis.Name == rightAxis.Name && leftAxis.Cardinality == AxisCardinality.Many)
        {
            var leftValues = left.When.Constraints[leftAxis.Name];
            var rightValues = right.When.Constraints[rightAxis.Name];
            var declared = leftAxis.Values.Select((value, index) => (value.Id, index)).ToArray();

            // A Many selection may contain values that satisfy either predicate.
            // For a candidate (left, right), selecting those two values is enough
            // to realize the outcome unless one is an earlier match for the other
            // predicate, which would lower that predicate's discriminator.
            var outcomes = new HashSet<(int Left, int Right)>();
            foreach (var (leftValue, _) in declared)
            {
                if (!leftValues.Contains(leftValue)) continue;
                foreach (var (rightValue, _) in declared)
                {
                    if (!rightValues.Contains(rightValue)) continue;
                    var selected = new[] { leftValue, rightValue };
                    var leftDiscriminator = selected.Select(leftAxis.IndexOf).Where(index => index >= 0 && leftValues.Contains(leftAxis.Values[index].Id)).DefaultIfEmpty(-1).Min();
                    var rightDiscriminator = selected.Select(rightAxis.IndexOf).Where(index => index >= 0 && rightValues.Contains(rightAxis.Values[index].Id)).DefaultIfEmpty(-1).Min();
                    if (outcomes.Add((leftDiscriminator, rightDiscriminator))) yield return (leftDiscriminator, rightDiscriminator);
                }
            }
            yield break;
        }
        yield return (-1, -1);
    }
}

public static class SelectionEnumerator
{
    public static long EstimatedCount(ImmutableArray<AxisDefinition> axes, long cap = long.MaxValue)
    {
        if (cap < 0) throw new ArgumentOutOfRangeException(nameof(cap));
        var total = 1L;
        foreach (var axis in axes)
        {
            var choices = axis.Cardinality == AxisCardinality.One
                ? axis.Values.Length
                : axis.Values.Length >= 63
                    ? (cap == long.MaxValue ? long.MaxValue : cap + 1)
                    : (1L << axis.Values.Length) - 1;
            if (choices == 0) return 0;
            if (total > cap / choices) return cap == long.MaxValue ? long.MaxValue : cap + 1;
            total *= choices;
        }
        return total;
    }

    /// Deterministic covering array used once exhaustive enumeration exceeds the
    /// report budget. Every value is paired with every value of every other axis;
    /// Many axes additionally include pair and full-set choices so discriminator
    /// changes are represented in the sample.
    public static ImmutableArray<VariantSelection> EnumeratePairwise(ImmutableArray<AxisDefinition> axes, int maximum = 4096) =>
        EnumeratePairwise(axes, maximum, out _);

    /// <summary>
    /// Enumerates a deterministic bounded pairwise sample. If the complete
    /// covering array does not fit, the returned prefix remains deterministic
    /// and <paramref name="truncated"/> is set instead of throwing.
    /// </summary>
    public static ImmutableArray<VariantSelection> EnumeratePairwise(ImmutableArray<AxisDefinition> axes, int maximum, out bool truncated)
    {
        if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum));
        var wasTruncated = false;

        // An axis with no values contributes no choices, and an axis name repeated in the
        // input would make ToDictionary throw. Sampling is a diagnostic aid, so it degrades to
        // ignoring a malformed axis rather than taking the whole publish gate down with an
        // exception; the axis itself is reported separately by CheckCoreAsync.
        axes = [.. axes.Where(x => !string.IsNullOrWhiteSpace(x.Name) && !x.Values.IsDefaultOrEmpty)
                       .GroupBy(x => x.Name, StringComparer.Ordinal)
                       .Select(x => x.First())];

        var choices = new Dictionary<string, ImmutableArray<ImmutableSortedSet<string>>>(StringComparer.Ordinal);
        foreach (var axis in axes)
        {
            var axisChoices = PairwiseChoices(axis);
            if (!axisChoices.IsDefaultOrEmpty) choices[axis.Name] = axisChoices;
        }
        axes = [.. axes.Where(x => choices.ContainsKey(x.Name))];

        var results = new List<VariantSelection>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var baseline = axes.ToDictionary(x => x.Name, x => choices[x.Name][0], StringComparer.Ordinal);
        Add(baseline);
        for (var i = 0; i < axes.Length && !wasTruncated; i++)
        {
            for (var j = i + 1; j < axes.Length && !wasTruncated; j++)
                foreach (var left in choices[axes[i].Name])
                {
                    if (wasTruncated) break;
                    foreach (var right in choices[axes[j].Name])
                    {
                        var selection = new Dictionary<string, ImmutableSortedSet<string>>(baseline, StringComparer.Ordinal)
                        {
                            [axes[i].Name] = left,
                            [axes[j].Name] = right
                        };
                        Add(selection);
                        if (wasTruncated) break;
                    }
                }
        }
        foreach (var axis in axes)
        {
            if (wasTruncated) break;
            foreach (var choice in choices[axis.Name])
            {
                var selection = new Dictionary<string, ImmutableSortedSet<string>>(baseline, StringComparer.Ordinal) { [axis.Name] = choice };
                Add(selection);
                if (wasTruncated) break;
            }
        }
        truncated = wasTruncated;
        return results.ToImmutableArray();

        void Add(IReadOnlyDictionary<string, ImmutableSortedSet<string>> values)
        {
            var selection = new VariantSelection { Axes = values.ToImmutableSortedDictionary(StringComparer.Ordinal) };
            if (!seen.Add(selection.ToCanonicalString())) return;
            if (results.Count >= maximum)
            {
                wasTruncated = true;
                return;
            }
            results.Add(selection);
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
        if (list.Length < 63)
        {
            var limit = 1UL << list.Length;
            for (var mask = 1UL; mask < limit; mask++) yield return list.Where((_, i) => (mask & (1UL << i)) != 0).ToImmutableSortedSet(StringComparer.Ordinal);
            yield break;
        }

        // Exhaustive subset enumeration is not bounded for large Many axes.
        // Retain every singleton, every pair, and the full set as a deterministic
        // bounded sample instead of recursing through an exponential space.
        foreach (var value in list) yield return ImmutableSortedSet.Create(StringComparer.Ordinal, value);
        for (var i = 0; i < list.Length; i++)
            for (var j = i + 1; j < list.Length; j++)
                yield return ImmutableSortedSet.Create(StringComparer.Ordinal, list[i], list[j]);
        yield return list.ToImmutableSortedSet(StringComparer.Ordinal);
    }
}
