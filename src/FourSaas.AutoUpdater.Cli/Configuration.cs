using System.Text.Json.Serialization;

namespace FourSaas.AutoUpdater.Cli;

public sealed record StorageConfiguration
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public int Priority { get; init; }
    public bool ReadOnly { get; init; }
    public string? ReadUrl { get; init; }
    public string? WriteUrl { get; init; }
}
public sealed record RepositoryAlias
{
    public required string Name { get; init; }
    public required string Address { get; init; }
}
public sealed record FourSupConfiguration
{
    public string? DefaultLocalRepository { get; init; }
    public string? DefaultServer { get; init; }
    public ImmutableArray<StorageConfiguration> Storages { get; init; } = [];
    public ImmutableArray<RepositoryAlias> Aliases { get; init; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed class ConfigurationLoader
{
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public IReadOnlyList<string> Layers { get; }
    public ConfigurationLoader(string? contentRoot = null)
    {
        var root = contentRoot ?? Environment.GetEnvironmentVariable("FOURSUP_CONTENT_ROOT") ?? Directory.GetCurrentDirectory();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var configuredPath = Environment.GetEnvironmentVariable("FOURSUP_CONFIG_PATH");
        Layers = configuredPath is null ? [Path.Combine(root, "4sup.json"), Path.Combine(appData, "4Story", "4sup", "config.json"), Path.Combine(common, "4Story", "4sup", "config.json")] : [Path.GetFullPath(configuredPath), Path.Combine(root, "4sup.json"), Path.Combine(appData, "4Story", "4sup", "config.json"), Path.Combine(common, "4Story", "4sup", "config.json")];
    }
    public FourSupConfiguration Load()
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in Layers)
        {
            if (!File.Exists(layer)) continue;
            using var document = JsonDocument.Parse(File.ReadAllBytes(layer));
            foreach (var property in document.RootElement.EnumerateObject()) if (!values.ContainsKey(property.Name)) values[property.Name] = property.Value.Clone();
        }
        var json = JsonSerializer.Serialize(values); var configuration = JsonSerializer.Deserialize<FourSupConfiguration>(json, _options) ?? new FourSupConfiguration();
        var aliases = configuration.Aliases.ToBuilder();
        if (!aliases.Any(x => x.Name == "local") && configuration.DefaultLocalRepository is not null) aliases.Add(new RepositoryAlias { Name = "local", Address = configuration.DefaultLocalRepository });
        if (!aliases.Any(x => x.Name == "remote") && configuration.DefaultServer is not null) aliases.Add(new RepositoryAlias { Name = "remote", Address = configuration.DefaultServer });
        if (aliases.Any(x => x.Name == "default")) throw new FormatException("The alias 'default' is reserved.");
        return configuration with { Aliases = aliases.ToImmutable() };
    }
    public async ValueTask SaveAsync(string key, JsonElement value, CancellationToken cancellationToken = default)
    {
        // Update the highest-precedence layer that already owns this key. New keys
        // belong in the outermost layer, even when a lower layer happens to exist.
        var target = Layers[0];
        foreach (var layer in Layers)
        {
            if (!File.Exists(layer)) continue;
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(layer, cancellationToken).ConfigureAwait(false));
            if (document.RootElement.TryGetProperty(key, out _)) { target = layer; break; }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Dictionary<string, JsonElement> values = [];
        if (File.Exists(target)) using (var existing = JsonDocument.Parse(await File.ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false))) foreach (var property in existing.RootElement.EnumerateObject()) values[property.Name] = property.Value.Clone();
        values[key] = value; await File.WriteAllTextAsync(target, JsonSerializer.Serialize(values, _options), cancellationToken).ConfigureAwait(false);
    }
}
