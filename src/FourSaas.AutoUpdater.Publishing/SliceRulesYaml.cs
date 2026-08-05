namespace FourSaas.AutoUpdater.Publishing;

/// <summary>Loads the intentionally small slice.yaml authoring format without coupling the runtime to a YAML engine.</summary>
public static class SliceRulesYaml
{
    public static SliceRules Parse(string yaml)
    {
        if (yaml.TrimStart().StartsWith('{')) return JsonSerializer.Deserialize<SliceRules>(yaml, RepositoryJson.Options) ?? throw new FormatException("Slice rules JSON is empty.");
        var source = string.Empty; var unmatched = true; var packages = new List<MutablePackage>(); MutablePackage? current = null; string? section = null;
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.Split('#')[0].TrimEnd(); if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
            var text = line.Trim();
            if (text.StartsWith("- id:", StringComparison.Ordinal)) { current = new MutablePackage { Id = new PackageId(Unquote(text[5..].Trim())) }; packages.Add(current); section = null; continue; }
            if (current is null)
            {
                if (text.StartsWith("source:", StringComparison.Ordinal)) source = Unquote(text[7..].Trim());
                else if (text.StartsWith("unmatched:", StringComparison.Ordinal)) unmatched = !text.EndsWith("ignore", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            var colon = text.IndexOf(':'); if (colon < 0) continue;
            var key = text[..colon].Trim().Trim('"'); var value = text[(colon + 1)..].Trim();
            if (value.Length == 0) { section = key; continue; }
            switch (key)
            {
                case "include": current.Include.AddRange(ParseList(value)); section = null; break;
                case "exclude": current.Exclude.AddRange(ParseList(value)); section = null; break;
                case "rewrite" when value.StartsWith('{'): foreach (var pair in ParseMap(value)) current.Rewrite[pair.Key] = pair.Value; section = null; break;
                case "policy" when value.StartsWith('{'): foreach (var pair in ParseMap(value)) current.Policies.Add(new SlicePolicy(pair.Key, ParsePolicy(pair.Value))); section = null; break;
                default:
                    if (section == "rewrite") current.Rewrite[key] = Unquote(value);
                    else if (section == "policy") current.Policies.Add(new SlicePolicy(key, ParsePolicy(value)));
                    break;
            }
        }
        if (string.IsNullOrWhiteSpace(source)) throw new FormatException("slice.yaml requires source.");
        return new SliceRules { SchemaVersion = 1, Source = source, UnmatchedIsError = unmatched, Packages = packages.Select(x => x.ToImmutable()).ToImmutableArray() };
    }

    private static IEnumerable<string> ParseList(string value) => value.Trim().TrimStart('[').TrimEnd(']').Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => Unquote(x.Trim()));
    private static IEnumerable<KeyValuePair<string, string>> ParseMap(string value) => value.Trim().TrimStart('{').TrimEnd('}').Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split(':', 2)).Where(x => x.Length == 2).Select(x => new KeyValuePair<string, string>(Unquote(x[0].Trim()), Unquote(x[1].Trim())));
    private static string Unquote(string value) => value.Trim().Trim('"', '\'');
    private static FileInstallPolicy ParsePolicy(string value) => Unquote(value).ToLowerInvariant() switch { "replace" => FileInstallPolicy.Replace, "preserve" => FileInstallPolicy.Preserve, "executable" => FileInstallPolicy.Executable, _ => throw new FormatException($"Unknown install policy '{value}'.") };

    private sealed class MutablePackage
    {
        public required PackageId Id { get; init; }
        public List<string> Include { get; } = [];
        public List<string> Exclude { get; } = [];
        public Dictionary<string, string> Rewrite { get; } = new(StringComparer.Ordinal);
        public List<SlicePolicy> Policies { get; } = [];
        public SlicePackageDefinition ToImmutable() => new() { Id = Id, Include = Include.ToImmutableArray(), Exclude = Exclude.ToImmutableArray(), Rewrite = Rewrite.ToImmutableDictionary(StringComparer.Ordinal), Policies = Policies.ToImmutableArray() };
    }
}
