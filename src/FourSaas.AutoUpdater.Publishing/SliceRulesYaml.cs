namespace FourSaas.AutoUpdater.Publishing;

/// <summary>
/// Loads the <c>slice.yaml</c> authoring format.
///
/// This is deliberately NOT a YAML parser. It accepts a small, fixed subset that looks like
/// YAML — scalars, flow sequences, flow mappings and one level of block nesting — so the
/// runtime does not have to carry a YAML engine. Anything outside that subset (anchors,
/// aliases, multi-line scalars, tags, nested block sequences) is not understood.
///
/// Because it looks like YAML, silently ignoring what it does not understand is the dangerous
/// failure mode: a file that a YAML editor renders one way would be interpreted another, with
/// no indication. Unrecognised and duplicated keys are therefore rejected rather than skipped,
/// so a construct this parser cannot represent fails loudly instead of quietly changing what
/// gets published. JSON input is accepted too, and is parsed properly.
/// </summary>
public static class SliceRulesYaml
{
    public static string JsonSchema => """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "https://4sup.invalid/schemas/slice.schema.json",
          "type": "object",
          "additionalProperties": false,
          "required": ["schemaVersion", "source", "packages", "unmatched"],
          "properties": {
            "schemaVersion": {"const": 1},
            "source": {"type": "string", "minLength": 1},
            "unmatched": {"enum": ["error", "ignore"]},
            "packages": {"type": "array", "items": {"$ref": "#/$defs/package"}}
          },
          "$defs": {
            "package": {
              "type": "object",
              "additionalProperties": false,
              "required": ["id", "include"],
              "properties": {
                "id": {"type": "string", "pattern": "^[a-z0-9](?:[a-z0-9]|[.-][a-z0-9])*$"},
                "include": {"type": "array", "items": {"type": "string"}},
                "exclude": {"type": "array", "items": {"type": "string"}},
                "rewrite": {"type": "object", "additionalProperties": {"type": "string"}},
                "policy": {"type": "object", "additionalProperties": {"enum": ["replace", "preserve", "executable"]}},
                "requires": {"type": "array", "items": {"$ref": "#/$defs/dependency"}}
              }
            },
            "dependency": {
              "type": "object",
              "additionalProperties": false,
              "required": ["id"],
              "properties": {
                "id": {"type": "string"},
                "minSequence": {"type": "integer", "minimum": 0},
                "maxSequence": {"type": "integer", "minimum": 0}
              }
            }
          }
        }
        """;

    public static string WithSchemaHeader(string yaml, Uri schemaUri) => $"# yaml-language-server: $schema={schemaUri}\n{yaml}";

    public static SliceRules Parse(string yaml)
    {
        if (yaml.TrimStart().StartsWith('{')) return JsonSerializer.Deserialize<SliceRules>(yaml, RepositoryJson.Options) ?? throw new FormatException("Slice rules JSON is empty.");
        var source = string.Empty; var unmatched = true; var packages = new List<MutablePackage>(); MutablePackage? current = null; string? section = null;
        var seenRootKeys = new HashSet<string>(StringComparer.Ordinal);
        var seenPackageKeys = new Dictionary<MutablePackage, HashSet<string>>();
        foreach (var raw in yaml.Split('\n'))
        {
            var line = StripComment(raw).TrimEnd(); if (string.IsNullOrWhiteSpace(line)) continue;
            var text = line.Trim();
            if (text.StartsWith("- id:", StringComparison.Ordinal) && (current is null || section is null)) { current = new MutablePackage { Id = new PackageId(Unquote(text[5..].Trim())) }; packages.Add(current); section = null; continue; }
            // A top-level key may legitimately appear after the packages block. Root keys are
            // written at column zero, so indentation distinguishes them from package keys;
            // without that they were interpreted as keys of whichever package came last.
            var isRootLevel = line.Length > 0 && !char.IsWhiteSpace(line[0]);
            if (isRootLevel && (text.StartsWith("source:", StringComparison.Ordinal) || text.StartsWith("unmatched:", StringComparison.Ordinal) || text.StartsWith("schemaVersion:", StringComparison.Ordinal) || text.StartsWith("packages:", StringComparison.Ordinal)))
            {
                current = null;
                section = null;
            }

            if (current is null)
            {
                if (text.StartsWith("source:", StringComparison.Ordinal)) { EnsureFirst(seenRootKeys, "source"); source = Unquote(text[7..].Trim()); }
                else if (text.StartsWith("unmatched:", StringComparison.Ordinal)) { EnsureFirst(seenRootKeys, "unmatched"); unmatched = !text.EndsWith("ignore", StringComparison.OrdinalIgnoreCase); }
                else if (text.StartsWith("schemaVersion:", StringComparison.Ordinal)) EnsureFirst(seenRootKeys, "schemaVersion");
                else if (!text.StartsWith("packages:", StringComparison.Ordinal))
                    throw new FormatException($"Unrecognised slice.yaml entry '{text}'. This format accepts a fixed subset; unknown keys are rejected rather than ignored.");
                continue;
            }
            if (section == "requires" && text.StartsWith("- ", StringComparison.Ordinal)) { current.Requires.Add(ParseDependency(text[2..])); continue; }
            var colon = text.IndexOf(':');
            if (colon < 0) throw new FormatException($"Unrecognised slice.yaml entry '{text}'.");
            var key = text[..colon].Trim().Trim('"'); var value = text[(colon + 1)..].Trim();
            if (value.Length == 0) { section = key; continue; }
            if (section is null && key is "include" or "exclude" or "rewrite" or "policy" or "requires")
                EnsureFirst(seenPackageKeys.TryGetValue(current, out var existing) ? existing : seenPackageKeys[current] = new HashSet<string>(StringComparer.Ordinal), key);
            switch (key)
            {
                case "include": current.Include.AddRange(ParseList(value)); section = null; break;
                case "exclude": current.Exclude.AddRange(ParseList(value)); section = null; break;
                case "rewrite" when value.StartsWith('{'): foreach (var pair in ParseMap(value)) current.Rewrite[pair.Key] = pair.Value; section = null; break;
                case "policy" when value.StartsWith('{'): foreach (var pair in ParseMap(value)) current.Policies.Add(new SlicePolicy(pair.Key, ParsePolicy(pair.Value))); section = null; break;
                case "requires": if (value.StartsWith("[", StringComparison.Ordinal)) current.Requires.AddRange(ParseDependencies(value)); section = null; break;
                default:
                    if (section == "rewrite") current.Rewrite[key] = Unquote(value);
                    else if (section == "policy") current.Policies.Add(new SlicePolicy(key, ParsePolicy(value)));
                    else throw new FormatException($"Unrecognised slice.yaml key '{key}'. This format accepts a fixed subset; unknown keys are rejected rather than ignored.");
                    break;
            }
        }
        if (string.IsNullOrWhiteSpace(source)) throw new FormatException("slice.yaml requires source.");
        return new SliceRules { SchemaVersion = 1, Source = source, UnmatchedIsError = unmatched, Packages = packages.Select(x => x.ToImmutable()).ToImmutableArray() };
    }

    /// <summary>
    /// Removes a trailing comment, ignoring '#' inside quotes.
    ///
    /// Splitting the line on '#' truncated any value legitimately containing one — a glob such
    /// as "assets/#tmp/**" silently became "assets/", quietly changing which files a package
    /// claims.
    /// </summary>
    private static string StripComment(string line)
    {
        var inSingle = false;
        var inDouble = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '\'' && !inDouble) inSingle = !inSingle;
            else if (character == '"' && !inSingle) inDouble = !inDouble;
            else if (character == '#' && !inSingle && !inDouble) return line[..index];
        }
        return line;
    }

    private static void EnsureFirst(HashSet<string> seen, string key)
    {
        if (!seen.Add(key)) throw new FormatException($"slice.yaml declares '{key}' more than once.");
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
        public List<PackageDependency> Requires { get; } = [];
        public SlicePackageDefinition ToImmutable() => new() { Id = Id, Include = Include.ToImmutableArray(), Exclude = Exclude.ToImmutableArray(), Rewrite = Rewrite.ToImmutableDictionary(StringComparer.Ordinal), Policies = Policies.ToImmutableArray(), Requires = Requires.ToImmutableArray() };
    }

    private static IEnumerable<PackageDependency> ParseDependencies(string value) => value.Trim().TrimStart('[').TrimEnd(']').Split("},", StringSplitOptions.RemoveEmptyEntries).Select(x => ParseDependency(x.Trim().Trim('{', '}', ' ')));
    private static PackageDependency ParseDependency(string value)
    {
        var pairs = ParseMap("{" + value.Trim().Trim('{', '}') + "}").ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        if (!pairs.TryGetValue("id", out var id) || !PackageId.TryCreate(id, out var packageId)) throw new FormatException("Slice dependency requires a valid id.");
        long? min = pairs.TryGetValue("minSequence", out var minText) && long.TryParse(minText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minValue) ? minValue : null;
        long? max = pairs.TryGetValue("maxSequence", out var maxText) && long.TryParse(maxText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxValue) ? maxValue : null;
        return new PackageDependency { Id = packageId, MinSequence = min, MaxSequence = max };
    }
}
