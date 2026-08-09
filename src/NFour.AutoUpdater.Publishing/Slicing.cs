namespace NFour.AutoUpdater.Publishing;

/// <summary>Assigns an install policy to source paths matching a glob.</summary><param name="Pattern">The source-path glob.</param><param name="Policy">The install policy.</param>
public sealed record SlicePolicy(string Pattern, FileInstallPolicy Policy);
/// <summary>Defines how one package is sliced from a build tree.</summary>
public sealed record SlicePackageDefinition
{
    /// <summary>Gets the output package identifier.</summary>
    public required PackageId Id { get; init; }
    /// <summary>Gets source-path globs included in the package.</summary>
    public ImmutableArray<string> Include { get; init; } = [];
    /// <summary>Gets source-path globs excluded from the package.</summary>
    public ImmutableArray<string> Exclude { get; init; } = [];
    /// <summary>Gets exact destination-path rewrites.</summary>
    public ImmutableDictionary<string, string> Rewrite { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// Prefix removed from a matched source path to produce its install path.
    /// </summary>
    /// <remarks>
    /// <see cref="Rewrite"/> maps one exact path to one exact path, which cannot express
    /// "this whole subtree installs one level up". Expressing that per file would mean one
    /// rewrite entry per file — for a build of this size, a configuration nobody can maintain
    /// and the thing the layout convention exists to avoid. A prefix covers a subtree of any
    /// size in one line.
    ///
    /// Applied before <see cref="Rewrite"/>, so an individual file can still be redirected
    /// afterwards.
    /// </remarks>
    public string? StripPrefix { get; init; }
    /// <summary>Gets ordered install-policy rules.</summary>
    public ImmutableArray<SlicePolicy> Policies { get; init; } = [];
    /// <summary>Gets package dependencies copied into the manifest.</summary>
    public ImmutableArray<PackageDependency> Requires { get; init; } = [];
}
/// <summary>Contains build-tree slicing configuration.</summary>
public sealed record SliceRules
{
    /// <summary>Gets the rules schema version.</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Gets the source build-tree root.</summary>
    public required string Source { get; init; }
    /// <summary>Gets package slicing definitions.</summary>
    public ImmutableArray<SlicePackageDefinition> Packages { get; init; } = [];
    /// <summary>Gets whether unmatched source entries produce errors.</summary>
    public bool UnmatchedIsError { get; init; } = true;
}
/// <summary>Represents one source entry assigned to a package.</summary><param name="SourcePath">The filesystem source path.</param><param name="Destination">The portable install path.</param><param name="Policy">The install policy.</param><param name="Kind">The entry kind.</param><param name="Mode">The optional POSIX mode.</param>
public sealed record SlicedFile(string SourcePath, VirtualPath Destination, FileInstallPolicy Policy, FileEntryKind Kind = FileEntryKind.File, string? Mode = null);
/// <summary>Contains files and dependencies assigned to one package.</summary><param name="Id">The package identifier.</param><param name="Files">The sliced entries.</param><param name="Requires">The package dependencies.</param>
public sealed record SlicedPackage(PackageId Id, ImmutableArray<SlicedFile> Files, ImmutableArray<PackageDependency> Requires);
/// <summary>Reports packages and diagnostics produced by slicing.</summary><param name="Packages">The sliced packages.</param><param name="Diagnostics">Slicing diagnostics.</param>
public sealed record SliceResult(ImmutableArray<SlicedPackage> Packages, ImmutableArray<Diagnostic> Diagnostics)
{
    /// <summary>Gets whether slicing completed without errors.</summary>
    public bool IsValid => !Diagnostics.Any(static x => x.IsError);
}

/// <summary>Safely classifies a build tree into package file sets.</summary>
public sealed class SliceEngine
{
    /// <summary>Slices a build tree according to validated rules.</summary>
    public async ValueTask<SliceResult> SliceAsync(SliceRules rules, CancellationToken cancellationToken = default)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var packages = rules.Packages.Select(x => new SlicedPackage(x.Id, [], x.Requires)).ToDictionary(x => x.Id);
        var filesByPackage = rules.Packages.ToDictionary(x => x.Id, _ => ImmutableArray.CreateBuilder<SlicedFile>());
        var source = Path.GetFullPath(rules.Source);
        if (!Directory.Exists(source)) return new([], [new("PKG014", DiagnosticSeverity.Error, $"Source directory '{source}' does not exist.")]);
        // The walk must not descend through links.  Enumerating recursively and checking only
        // the leaf lets an ordinary file reached via a symlinked parent pass the check, so a
        // link in the source tree could pull secrets or unrelated files into a package.
        foreach (var (file, isLink) in LinkSafeDirectoryWalk.EnumerateFileEntries(source).OrderBy(static x => x.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/');
            if (isLink)
            {
                diagnostics.Add(new("PKG012", DiagnosticSeverity.Error, $"Symlink or reparse-point source '{relative}' is not publishable.", relative));
                continue;
            }
            var matches = rules.Packages.Where(def => def.Include.Any(pattern => Glob.IsMatch(relative, pattern)) && !def.Exclude.Any(pattern => Glob.IsMatch(relative, pattern))).ToArray();
            if (matches.Length == 0)
            {
                if (rules.UnmatchedIsError) diagnostics.Add(new("PKG014", DiagnosticSeverity.Error, $"Source file '{relative}' matched no package.", relative));
                continue;
            }
            if (matches.Length > 1) diagnostics.Add(new("PKG014b", DiagnosticSeverity.Error, $"Source file '{relative}' matched multiple packages: {string.Join(", ", matches.Select(x => x.Id))}.", relative));
            foreach (var definition in matches)
            {
                var destinationText = SlicePaths.Strip(relative, definition.StripPrefix);
                destinationText = definition.Rewrite.TryGetValue(destinationText, out var rewritten) ? rewritten
                    : definition.Rewrite.TryGetValue(relative, out var legacyRewritten) ? legacyRewritten
                    : destinationText;
                if (destinationText.Length == 0)
                {
                    diagnostics.Add(new("PKG017", DiagnosticSeverity.Error, $"Source file '{relative}' has nothing left after stripping prefix '{definition.StripPrefix}'.", relative));
                    continue;
                }
                if (!VirtualPath.TryCreate(destinationText, out var destination, out var error)) { diagnostics.Add(new("PKG012", DiagnosticSeverity.Error, error ?? "Invalid path.", relative)); continue; }
                var policy = definition.Policies.Where(x => Glob.IsMatch(relative, x.Pattern)).Select(x => x.Policy).LastOrDefault();
                filesByPackage[definition.Id].Add(new SlicedFile(file, destination, policy));
            }
        }
        // Empty directories are explicit package entries. Non-empty directories are
        // materialised by their file entries and do not need redundant rows.
        foreach (var (directory, isLinkedDirectory) in LinkSafeDirectoryWalk.EnumerateDirectoryEntries(source)
                     .Where(x => x.IsLink || !Directory.EnumerateFileSystemEntries(x.Path).Any())
                     .OrderBy(static x => x.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, directory).Replace(Path.DirectorySeparatorChar, '/');
            if (isLinkedDirectory)
            {
                diagnostics.Add(new("PKG012", DiagnosticSeverity.Error, $"Symlink or reparse-point source directory '{relative}' is not publishable.", relative));
                continue;
            }
            var matches = rules.Packages.Where(def => def.Include.Any(pattern => Glob.IsMatch(relative, pattern)) && !def.Exclude.Any(pattern => Glob.IsMatch(relative, pattern))).ToArray();
            if (matches.Length == 0)
            {
                if (rules.UnmatchedIsError) diagnostics.Add(new("PKG014", DiagnosticSeverity.Error, $"Source directory '{relative}' matched no package.", relative));
                continue;
            }
            if (matches.Length > 1) diagnostics.Add(new("PKG014b", DiagnosticSeverity.Error, $"Source directory '{relative}' matched multiple packages: {string.Join(", ", matches.Select(x => x.Id))}.", relative));
            foreach (var definition in matches)
            {
                var destinationText = SlicePaths.Strip(relative, definition.StripPrefix);
                destinationText = definition.Rewrite.TryGetValue(destinationText, out var rewritten) ? rewritten
                    : definition.Rewrite.TryGetValue(relative, out var legacyRewritten) ? legacyRewritten
                    : destinationText;
                if (destinationText.Length == 0) continue;
                if (!VirtualPath.TryCreate(destinationText, out var destination, out var error)) { diagnostics.Add(new("PKG012", DiagnosticSeverity.Error, error ?? "Invalid path.", relative)); continue; }
                filesByPackage[definition.Id].Add(new SlicedFile(directory, destination, FileInstallPolicy.Replace, FileEntryKind.Directory));
            }
        }
        var output = ImmutableArray.CreateBuilder<SlicedPackage>();
        foreach (var definition in rules.Packages)
        {
            var files = filesByPackage[definition.Id].ToImmutable();
            foreach (var group in files.GroupBy(x => x.Destination.Value, StringComparer.Ordinal))
                if (group.Count() > 1) diagnostics.Add(new("PKG009", DiagnosticSeverity.Error, $"Package '{definition.Id}' contains duplicate path '{group.Key}'.", definition.Id.Value));
            foreach (var group in files.GroupBy(x => x.Destination.FoldedKey, StringComparer.Ordinal))
                if (group.Select(x => x.Destination.Value).Distinct(StringComparer.Ordinal).Count() > 1) diagnostics.Add(new("PKG009", DiagnosticSeverity.Error, $"Package '{definition.Id}' contains case-only path collision at '{group.Key}'.", definition.Id.Value));
            output.Add(new SlicedPackage(definition.Id, files, definition.Requires));
        }
        await Task.CompletedTask;
        return new SliceResult(output.ToImmutable(), diagnostics.ToImmutable());
    }
}

/// <summary>Transforms source paths into install paths.</summary>
public static class SlicePaths
{
    /// <summary>
    /// Removes <paramref name="prefix"/> from the front of <paramref name="path"/>.
    /// Returns the path unchanged when the prefix does not apply, so a rule with a prefix can
    /// still match files outside it without silently mangling them.
    /// </summary>
    public static string Strip(string path, string? prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return path;
        var normalised = prefix.Trim('/');
        if (normalised.Length == 0) return path;
        if (!path.StartsWith(normalised, StringComparison.Ordinal)) return path;
        if (path.Length == normalised.Length) return string.Empty;
        return path[normalised.Length] == '/' ? path[(normalised.Length + 1)..] : path;
    }
}

/// <summary>Matches portable paths against cached package globs.</summary>
public static class Glob
{
    // Patterns are few and reused across every file in the build; paths number in the
    // hundreds of thousands. Rebuilding and re-parsing the regex per (file, pattern) pair made
    // matching the dominant cost of slicing a large tree.
    private static readonly ConcurrentDictionary<string, System.Text.RegularExpressions.Regex> Compiled = new(StringComparer.Ordinal);

    /// <summary>Determines whether a path matches a glob pattern.</summary>
    public static bool IsMatch(string path, string pattern)
        => Compiled.GetOrAdd(pattern, Build).IsMatch(path);

    private static System.Text.RegularExpressions.Regex Build(string pattern)
    {
        var regex = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*') { regex.Append(".*"); i++; }
            else if (c == '*') regex.Append("[^/]*");
            else if (c == '?') regex.Append("[^/]");
            else regex.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString()));
        }
        regex.Append("$");
        return new System.Text.RegularExpressions.Regex(regex.ToString(), System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);
    }
}
