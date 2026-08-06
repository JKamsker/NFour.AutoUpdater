namespace FourSaas.AutoUpdater.Publishing;

public sealed record SlicePolicy(string Pattern, FileInstallPolicy Policy);
public sealed record SlicePackageDefinition
{
    public required PackageId Id { get; init; }
    public ImmutableArray<string> Include { get; init; } = [];
    public ImmutableArray<string> Exclude { get; init; } = [];
    public ImmutableDictionary<string, string> Rewrite { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableArray<SlicePolicy> Policies { get; init; } = [];
    public ImmutableArray<PackageDependency> Requires { get; init; } = [];
}
public sealed record SliceRules
{
    public int SchemaVersion { get; init; } = 1;
    public required string Source { get; init; }
    public ImmutableArray<SlicePackageDefinition> Packages { get; init; } = [];
    public bool UnmatchedIsError { get; init; } = true;
}
public sealed record SlicedFile(string SourcePath, VirtualPath Destination, FileInstallPolicy Policy, FileEntryKind Kind = FileEntryKind.File, string? Mode = null);
public sealed record SlicedPackage(PackageId Id, ImmutableArray<SlicedFile> Files, ImmutableArray<PackageDependency> Requires);
public sealed record SliceResult(ImmutableArray<SlicedPackage> Packages, ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsValid => !Diagnostics.Any(static x => x.IsError);
}

public sealed class SliceEngine
{
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
                var destinationText = definition.Rewrite.TryGetValue(relative, out var rewritten) ? rewritten : relative;
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
                var destinationText = definition.Rewrite.TryGetValue(relative, out var rewritten) ? rewritten : relative;
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

public static class Glob
{
    public static bool IsMatch(string path, string pattern)
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
        return System.Text.RegularExpressions.Regex.IsMatch(path, regex.ToString(), System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
}
