using System.Buffers;

namespace NFour.AutoUpdater.Core;

/// <summary>Resolves release requirements into a deterministic package selection.</summary>
public sealed class VariantResolver : IVariantResolver
{
    /// <inheritdoc />
    public ResolutionResult Resolve(ReleaseLock release, VariantSelection requested)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var axes = release.Axes.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var bound = ImmutableSortedDictionary.CreateBuilder<string, ImmutableSortedSet<string>>(StringComparer.Ordinal);

        foreach (var unknown in requested.Axes.Keys.Where(x => !axes.ContainsKey(x)))
            diagnostics.Add(new("SEL004", DiagnosticSeverity.Warning, $"Axis '{unknown}' is not declared by this release and was ignored.", unknown));

        foreach (var axis in release.Axes.OrderBy(static x => x.Rank))
        {
            var values = requested[axis.Name];
            if (values.IsEmpty)
            {
                if (axis.Default is not null) values = ImmutableSortedSet.Create(StringComparer.Ordinal, axis.Default);
                else if (axis.Required)
                {
                    diagnostics.Add(new("SEL001", DiagnosticSeverity.Error, $"Required axis has no value. Legal values: {string.Join(", ", axis.Values.Select(x => x.Id))}.", axis.Name));
                    continue;
                }
            }

            var resolved = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                if (!axis.TryResolveRetired(value, out var replacement, out var wasRetired, out var retirementError))
                {
                    diagnostics.Add(new("SEL002", DiagnosticSeverity.Error, retirementError ?? $"Unknown value '{value}'.", axis.Name));
                    continue;
                }
                if (wasRetired) diagnostics.Add(new("SEL005", DiagnosticSeverity.Info, $"Value '{value}' was retired and remapped to '{replacement}'.", axis.Name));
                if (axis.IndexOf(replacement) < 0)
                    diagnostics.Add(new("SEL002", DiagnosticSeverity.Error, $"Unknown value '{replacement}'. Legal values: {string.Join(", ", axis.Values.Select(x => x.Id))}.", axis.Name));
                else resolved.Add(replacement);
            }
            if (axis.Cardinality == AxisCardinality.One && resolved.Count > 1)
                diagnostics.Add(new("SEL003", DiagnosticSeverity.Error, "An axis with cardinality 'one' may contain only one value.", axis.Name));
            if (resolved.Count > 0) bound[axis.Name] = resolved.ToImmutable();
        }

        var selection = new VariantSelection { Axes = bound.ToImmutable() };
        var pins = release.Packages.ToDictionary(x => x.Id, x => x, EqualityComparer<PackageId>.Default);
        var matched = new Dictionary<PackageId, List<(PackageRequirement Requirement, int Layer, int Discriminator)>>();
        foreach (var requirement in release.Requirements)
        {
            if (!requirement.When.Matches(selection)) continue;
            if (!pins.TryGetValue(requirement.Package, out var pin))
            {
                diagnostics.Add(new(requirement.Optional ? "RES001" : "RES001", requirement.Optional ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error, $"Requirement references missing package '{requirement.Package}'.", requirement.Package.Value));
                continue;
            }
            var layer = DeriveLayer(requirement, axes, selection, diagnostics);
            var discriminator = DeriveDiscriminator(requirement, axes, selection);
            if (!matched.TryGetValue(pin.Id, out var entries)) matched[pin.Id] = entries = [];
            entries.Add((requirement, layer, discriminator));
        }

        var selected = ImmutableArray.CreateBuilder<ResolvedPackage>();
        foreach (var (id, entries) in matched)
        {
            var first = entries[0];
            if (entries.Skip(1).Any(x => x.Layer != first.Layer || x.Discriminator != first.Discriminator || x.Requirement.Optional != first.Requirement.Optional || x.Requirement.RankAs != first.Requirement.RankAs || x.Requirement.LayerOverride != first.Requirement.LayerOverride))
                diagnostics.Add(new("PKG008", DiagnosticSeverity.Error, $"Package '{id}' is matched by requirements with inconsistent precedence.", id.Value));
            var requirement = first.Requirement with { Overrides = entries.SelectMany(x => x.Requirement.Overrides).Distinct().ToImmutableArray() };
            var winner = entries.OrderByDescending(x => x.Layer).ThenByDescending(x => x.Discriminator).First();
            selected.Add(new ResolvedPackage(requirement, pins[id], winner.Layer, winner.Discriminator));
        }

        var selectedById = selected.ToDictionary(x => x.Id);
        foreach (var package in selected)
        {
            foreach (var dependency in package.Pin.Requires)
            {
                var dependencySequence = selectedById.TryGetValue(dependency.Id, out var selectedDependency) ? (selectedDependency.Pin.Sequence == 0 ? selectedDependency.Pin.Version.Sequence : selectedDependency.Pin.Sequence) : -1;
                if (dependencySequence < 0 || dependency.MinSequence is { } min && dependencySequence < min || dependency.MaxSequence is { } max && dependencySequence > max)
                    diagnostics.Add(new("RES002", DiagnosticSeverity.Error, $"Package '{package.Id}' has unsatisfied dependency '{dependency.Id}'.", package.Id.Value));
            }
            foreach (var conflict in package.Pin.Conflicts)
                if (selectedById.ContainsKey(conflict)) diagnostics.Add(new("RES005", DiagnosticSeverity.Error, $"Package '{package.Id}' conflicts with selected package '{conflict}'.", package.Id.Value));
        }

        var ordered = selected.OrderBy(x => x.Layer).ThenBy(x => x.Discriminator).ThenBy(x => x.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        return new ResolutionResult { Selection = selection, Packages = ordered, Diagnostics = diagnostics.ToImmutable() };
    }

    private static int DeriveLayer(PackageRequirement requirement, IReadOnlyDictionary<string, AxisDefinition> axes, VariantSelection selection, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (requirement.LayerOverride is { } overrideLayer)
        {
            diagnostics.Add(new("PKG010", DiagnosticSeverity.Warning, "An explicit layer override was used.", requirement.Package.Value));
            return overrideLayer;
        }
        if (requirement.RankAs is { } rankAs)
        {
            if (!axes.TryGetValue(rankAs, out var axis))
            {
                diagnostics.Add(new("PKG003", DiagnosticSeverity.Error, $"RankAs axis '{rankAs}' is not declared.", requirement.Package.Value));
                return 0;
            }
            return axis.Rank * 1000;
        }
        if (requirement.When.IsAlways) return 0;
        var constrained = requirement.When.Constraints.Keys.Where(axes.ContainsKey).Select(x => axes[x]).MaxBy(static x => x.Rank);
        return constrained?.Rank * 1000 ?? 0;
    }

    private static int DeriveDiscriminator(PackageRequirement requirement, IReadOnlyDictionary<string, AxisDefinition> axes, VariantSelection selection)
    {
        if (requirement.RankAs is not null || requirement.When.IsAlways) return -1;
        var dominant = requirement.When.Constraints.Keys.Where(axes.ContainsKey).Select(x => axes[x]).MaxBy(static x => x.Rank);
        if (dominant is null) return -1;
        var values = selection[dominant.Name].Intersect(requirement.When.Constraints[dominant.Name]);
        return values.Select(dominant.IndexOf).Where(static x => x >= 0).DefaultIfEmpty(-1).Min();
    }
}

/// <summary>Composes resolved package file tables using declared precedence and overrides.</summary>
public sealed class FileSetComposer : IFileSetComposer
{
    /// <inheritdoc />
    public async ValueTask<ComposedFileSet> ComposeAsync(IPackageRepository repository, ResolutionResult resolution, CancellationToken cancellationToken = default)
    {
        var diagnostics = resolution.Diagnostics.ToBuilder();
        var files = new Dictionary<VirtualPath, ComposedFile>();
        var foldedPaths = new Dictionary<string, VirtualPath>(StringComparer.Ordinal);
        var shadowed = ImmutableArray.CreateBuilder<ComposedFile>();
        var overrides = resolution.Packages.ToDictionary(x => x.Id, x => x.Requirement.Overrides.ToImmutableHashSet());

        foreach (var resolved in resolution.Packages)
        {
            var requestedVersion = new PackageVersion(resolved.Pin.Version.Label, resolved.Pin.Sequence);
            var manifest = repository is IManifestDigestRepository verifiedRepository && resolved.Pin.ManifestDigest.IsValid
                ? await verifiedRepository.GetManifestAsync(resolved.Pin.Id, requestedVersion, resolved.Pin.ManifestDigest, cancellationToken).ConfigureAwait(false)
                : await repository.GetManifestAsync(resolved.Pin.Id, requestedVersion, cancellationToken).ConfigureAwait(false);
            if (manifest is null)
            {
                diagnostics.Add(new("RES001", DiagnosticSeverity.Error, $"Manifest for '{resolved.Id}' was not found.", resolved.Id.Value));
                continue;
            }
            await foreach (var entry in repository.ReadFileTableAsync(manifest, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (!VirtualPath.TryCreate(entry.Path.Value, out var path, out _))
                {
                    diagnostics.Add(new("RES003", DiagnosticSeverity.Error, $"Manifest contains illegal path '{entry.Path.Value}'.", resolved.Id.Value));
                    continue;
                }
                var candidate = new ComposedFile(path, entry.Content, entry.Size, resolved.Id, entry.Policy, entry.Kind, entry.Mode, resolved.Layer, resolved.Discriminator);
                if (foldedPaths.TryGetValue(path.FoldedKey, out var foldedPath) && foldedPath != path)
                {
                    diagnostics.Add(new("PKG009", DiagnosticSeverity.Error, $"Case-folded path collision between '{foldedPath}' and '{path}'.", resolved.Id.Value));
                    continue;
                }
                foldedPaths[path.FoldedKey] = path;
                if (!files.TryGetValue(path, out var incumbent))
                {
                    files[path] = candidate;
                    continue;
                }

                var order = Compare(candidate, incumbent);
                if (order <= 0) continue;
                if (candidate.Layer > incumbent.Layer)
                {
                    if (!overrides[candidate.Owner].Contains(incumbent.Owner)) diagnostics.Add(new("RES006", DiagnosticSeverity.Error, $"'{candidate.Owner}' shadows '{incumbent.Owner}' without declaring an override.", path.Value));
                }
                else if (candidate.Discriminator > incumbent.Discriminator)
                {
                    if (!overrides[candidate.Owner].Contains(incumbent.Owner)) diagnostics.Add(new("RES007", DiagnosticSeverity.Error, $"'{candidate.Owner}' collides with '{incumbent.Owner}' within an axis without declaring an override.", path.Value));
                }
                else diagnostics.Add(new("RES004", DiagnosticSeverity.Error, $"Path is owned by two packages at the same precedence: '{incumbent.Owner}' and '{candidate.Owner}'.", path.Value));
                shadowed.Add(incumbent);
                files[path] = candidate;
            }
        }

        var immutable = files.ToImmutableSortedDictionary(x => x.Key, x => x.Value);
        return new ComposedFileSet { Files = immutable, Shadowed = shadowed.ToImmutable(), FileSetId = FileSetIdentity.Compute(immutable), Diagnostics = diagnostics.ToImmutable() };
    }

    private static int Compare(ComposedFile left, ComposedFile right) =>
        left.Layer != right.Layer ? left.Layer.CompareTo(right.Layer) :
        left.Discriminator != right.Discriminator ? left.Discriminator.CompareTo(right.Discriminator) :
        StringComparer.Ordinal.Compare(left.Owner.Value, right.Owner.Value);
}

/// <summary>Computes the deterministic identity of a composed install file set.</summary>
public static class FileSetIdentity
{
    /// <summary>Hashes the canonical path, content, owner, and install metadata of each file.</summary>
    /// <param name="files">The composed files keyed by path.</param>
    /// <returns>The file-set digest.</returns>
    public static ContentHash Compute(IReadOnlyDictionary<VirtualPath, ComposedFile> files)
    {
        var buffer = new ArrayBufferWriter<byte>();
        foreach (var file in files.OrderBy(x => x.Key.Value, StringComparer.Ordinal).Select(x => x.Value))
        {
            var line = $"{file.Path.Value}\0{file.Content}\0{file.Owner.Value}\0{Policy(file.Policy)}\0{Kind(file.Kind)}\0{file.Mode ?? string.Empty}\n";
            var bytes = Encoding.UTF8.GetBytes(line.Normalize(NormalizationForm.FormC));
            buffer.Write(bytes);
        }
        return ContentHash.Compute(buffer.WrittenSpan);
    }

    private static string Policy(FileInstallPolicy value) => value switch
    {
        FileInstallPolicy.Replace => "replace",
        FileInstallPolicy.Preserve => "preserve",
        FileInstallPolicy.Executable => "executable",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
    private static string Kind(FileEntryKind value) => value == FileEntryKind.File ? "file" : "dir";
}
