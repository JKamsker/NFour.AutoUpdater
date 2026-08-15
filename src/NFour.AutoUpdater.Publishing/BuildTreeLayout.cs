namespace NFour.AutoUpdater.Publishing;

/// <summary>One file in a build tree, classified by the layout convention.</summary>
/// <param name="SourcePath">Path relative to the build output root.</param>
/// <param name="Axis">Axis that selects this file, or null when it is axis-independent.</param>
/// <param name="Value">Axis value that selects this file, or null when axis-independent.</param>
/// <param name="InstallPath">Path the file occupies in an install.</param>
public readonly record struct ClassifiedBuildFile(string SourcePath, string? Axis, string? Value, string InstallPath);

/// <summary>
/// The build-output layout convention, and the checks that keep a build honest about it.
///
/// The convention exists to answer one question mechanically: <em>which package does this file
/// belong to?</em> A build that answers it by directory needs about a dozen slice rules. A
/// build that answers it by filename patterns, or not at all, needs per-file rules — and a
/// packaging configuration with 200,000 hand-maintained entries is one that silently rots
/// until packages start shipping the wrong files.
///
/// <code>
/// &lt;build-output&gt;/
///   common/                          axis-independent content
///   axis/&lt;axis&gt;/&lt;value&gt;/   content selected by one axis value
/// </code>
///
/// The install path is whatever follows the classifying directory, so
/// <c>axis/lang/de/data/strings.bin</c> installs as <c>data/strings.bin</c>. That is the only
/// transformation, and it is expressible as a single <c>stripPrefix</c> per rule.
///
/// The checks below are the reason the convention is worth adopting rather than merely
/// describing. They run over paths alone, so they are cheap enough to sit in the build.
/// </summary>
public static class BuildTreeLayout
{
    /// <summary>Gets the directory containing axis-independent build content.</summary>
    public const string CommonRoot = "common";
    /// <summary>Gets the directory containing axis-selected build content.</summary>
    public const string AxisRoot = "axis";

    /// <summary>
    /// Classifies one build-relative path, or returns null when it does not fit the
    /// convention.
    /// </summary>
    public static ClassifiedBuildFile? Classify(string relativePath)
    {
        var path = relativePath.Replace('\\', '/').TrimStart('/');
        var segments = path.Split('/');

        if (segments.Length >= 2 && segments[0] == CommonRoot)
            return new ClassifiedBuildFile(path, null, null, string.Join('/', segments[1..]));

        // axis / <axis> / <value> / <install path…> — four segments minimum, because a file
        // directly inside an axis-value directory still needs a name.
        if (segments.Length >= 4 && segments[0] == AxisRoot)
            return new ClassifiedBuildFile(path, segments[1], segments[2], string.Join('/', segments[3..]));

        return null;
    }

    /// <summary>
    /// Checks a build tree against the convention.
    /// </summary>
    /// <param name="relativePaths">Every file path relative to the build output root.</param>
    /// <param name="declaredAxes">
    /// Axis names the release declares. Anything else found under <c>axis/</c> is reported,
    /// since a typo there would otherwise produce a package nothing ever selects.
    /// </param>
    public static ImmutableArray<Diagnostic> Validate(IEnumerable<string> relativePaths, IReadOnlyCollection<string>? declaredAxes = null)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var classified = new List<ClassifiedBuildFile>();

        foreach (var path in relativePaths)
        {
            if (Classify(path) is { } entry) { classified.Add(entry); continue; }

            // LAY001 is the check that keeps the convention true over time. Without it a file
            // added outside the structure is simply unclassified, and the first symptom is a
            // package missing content nobody notices until players report it.
            diagnostics.Add(new("LAY001", DiagnosticSeverity.Error,
                $"Build file '{path}' is neither under '{CommonRoot}/' nor under '{AxisRoot}/<axis>/<value>/'.", path));
        }

        if (declaredAxes is { Count: > 0 })
        {
            var known = declaredAxes.ToHashSet(StringComparer.Ordinal);
            foreach (var axis in classified.Where(x => x.Axis is not null).Select(x => x.Axis!).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
                if (!known.Contains(axis))
                    diagnostics.Add(new("LAY002", DiagnosticSeverity.Error,
                        $"Build tree contains axis '{axis}', which the release does not declare.", axis));
        }

        // LAY003 — cross-axis disjointness. Two axes claiming the same install path means
        // switching one axis rewrites files the other owns, so a language change would drag
        // down UI content. Values *within* one axis are alternatives and are expected to
        // overlap, so only distinct owners are compared.
        var ownersByInstallPath = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var entry in classified)
        {
            var owner = entry.Axis ?? CommonRoot;
            if (!ownersByInstallPath.TryGetValue(entry.InstallPath, out var owners))
                ownersByInstallPath[entry.InstallPath] = owners = new SortedSet<string>(StringComparer.Ordinal);
            owners.Add(owner);
        }
        foreach (var (installPath, owners) in ownersByInstallPath.Where(x => x.Value.Count > 1).OrderBy(x => x.Key, StringComparer.Ordinal))
            diagnostics.Add(new("LAY003", DiagnosticSeverity.Error,
                $"Install path '{installPath}' is claimed by more than one owner ({string.Join(", ", owners)}); switchable axes must stay path-disjoint.", installPath));

        // LAY004 — a value of an axis missing a path its siblings provide. Usually a
        // translation or asset that did not get exported, which would otherwise ship as a
        // variant silently missing a file.
        foreach (var axisGroup in classified.Where(x => x.Axis is not null).GroupBy(x => x.Axis!, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var byValue = axisGroup.GroupBy(x => x.Value!, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Select(y => y.InstallPath).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
            if (byValue.Count < 2) continue;
            var union = byValue.Values.SelectMany(x => x).ToHashSet(StringComparer.Ordinal);
            foreach (var (value, provided) in byValue.OrderBy(x => x.Key, StringComparer.Ordinal))
                foreach (var missing in union.Except(provided).OrderBy(x => x, StringComparer.Ordinal))
                    diagnostics.Add(new("LAY004", DiagnosticSeverity.Warning,
                        $"Axis '{axisGroup.Key}' value '{value}' does not provide '{missing}', which other values of the same axis do.", $"{axisGroup.Key}/{value}"));
        }

        return diagnostics.ToImmutable();
    }

    /// <summary>
    /// Generates the slice rules a conforming build tree needs: one package per axis value
    /// plus one for the common tree.
    /// </summary>
    /// <remarks>
    /// Derived from the tree rather than hand-written, so adding a language is a build-output
    /// change and nothing else. The output is intended to be checked in and reviewed, not
    /// generated silently at publish time — a diff is how a reviewer notices that a package
    /// appeared or vanished.
    /// </remarks>
    public static ImmutableArray<SlicePackageDefinition> GenerateRules(IEnumerable<string> relativePaths, string basePackageId = "game.base")
    {
        var classified = relativePaths.Select(Classify).Where(x => x is not null).Select(x => x!.Value).ToArray();
        var rules = ImmutableArray.CreateBuilder<SlicePackageDefinition>();

        if (classified.Any(x => x.Axis is null) && PackageId.TryCreate(basePackageId, out var baseId))
            rules.Add(new SlicePackageDefinition
            {
                Id = baseId,
                Include = [$"{CommonRoot}/**"],
                StripPrefix = CommonRoot
            });

        foreach (var group in classified.Where(x => x.Axis is not null)
                     .GroupBy(x => (Axis: x.Axis!, Value: x.Value!))
                     .OrderBy(x => x.Key.Axis, StringComparer.Ordinal).ThenBy(x => x.Key.Value, StringComparer.Ordinal))
        {
            var prefix = $"{AxisRoot}/{group.Key.Axis}/{group.Key.Value}";
            if (!PackageId.TryCreate($"{group.Key.Axis}.{group.Key.Value}", out var id)) continue;
            rules.Add(new SlicePackageDefinition
            {
                Id = id,
                Include = [$"{prefix}/**"],
                StripPrefix = prefix
            });
        }

        return rules.ToImmutable();
    }
}
