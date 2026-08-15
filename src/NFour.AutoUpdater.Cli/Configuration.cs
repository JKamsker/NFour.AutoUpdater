using System.Text.Json.Serialization;

namespace NFour.AutoUpdater.Cli;

/// <summary>Describes a named storage backend available to the CLI.</summary>
public sealed record StorageConfiguration
{
    /// <summary>Gets the configuration-local storage name.</summary>
    public required string Name { get; init; }
    /// <summary>Gets the storage provider type.</summary>
    public required string Type { get; init; }
    /// <summary>Gets the order in which the storage should be considered.</summary>
    public int Priority { get; init; }
    /// <summary>Gets whether write operations are disabled for the storage.</summary>
    public bool ReadOnly { get; init; }
    /// <summary>Gets the optional read endpoint.</summary>
    public string? ReadUrl { get; init; }
    /// <summary>Gets the optional write endpoint.</summary>
    public string? WriteUrl { get; init; }
}

/// <summary>Associates a short repository name with its repository address.</summary>
public sealed record RepositoryAlias
{
    /// <summary>Gets the alias name.</summary>
    public required string Name { get; init; }
    /// <summary>Gets the repository address resolved by the alias.</summary>
    public required string Address { get; init; }
}

/// <summary>Represents the merged configuration consumed by the CLI.</summary>
public sealed record FourSupConfiguration
{
    /// <summary>Gets the legacy default local repository address.</summary>
    public string? DefaultLocalRepository { get; init; }
    /// <summary>Gets the legacy default management-server address.</summary>
    public string? DefaultServer { get; init; }
    /// <summary>Gets the configured storage backends.</summary>
    public ImmutableArray<StorageConfiguration> Storages { get; init; } = [];
    /// <summary>Gets the configured repository aliases.</summary>
    public ImmutableArray<RepositoryAlias> Aliases { get; init; } = [];
    /// <summary>Gets unrecognized configuration values preserved for forward compatibility.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; init; }
}

/// <summary>Loads and updates the CLI's layered JSON configuration.</summary>
public sealed class ConfigurationLoader
{
    private const string ContentRootEnvironmentVariable = "FOURSUP_CONTENT_ROOT";
    private const string ConfigurationPathEnvironmentVariable = "FOURSUP_CONFIG_PATH";
    private const string RootConfigurationFileName = "4sup.json";
    private const string VendorDirectoryName = "4Story";
    private const string ProductDirectoryName = "4sup";
    private const string UserConfigurationFileName = "config.json";
    private const string LocalRepositoryAlias = "local";
    private const string RemoteRepositoryAlias = "remote";
    private const string ReservedDefaultAlias = "default";

    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Gets configuration files in descending precedence order.</summary>
    public IReadOnlyList<string> Layers { get; }

    /// <summary>Initializes a loader rooted at the supplied content directory.</summary>
    /// <param name="contentRoot">Optional content root used for the repository-local layer.</param>
    public ConfigurationLoader(string? contentRoot = null)
    {
        var root = contentRoot ?? Environment.GetEnvironmentVariable(ContentRootEnvironmentVariable) ?? Directory.GetCurrentDirectory();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var configuredPath = Environment.GetEnvironmentVariable(ConfigurationPathEnvironmentVariable);
        var rootLayer = Path.Combine(root, RootConfigurationFileName);
        var userLayer = Path.Combine(appData, VendorDirectoryName, ProductDirectoryName, UserConfigurationFileName);
        var machineLayer = Path.Combine(common, VendorDirectoryName, ProductDirectoryName, UserConfigurationFileName);
        Layers = configuredPath is null
            ? [rootLayer, userLayer, machineLayer]
            : [Path.GetFullPath(configuredPath), rootLayer, userLayer, machineLayer];
    }

    /// <summary>Loads and merges all existing configuration layers.</summary>
    /// <returns>The merged configuration.</returns>
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
        if (!aliases.Any(x => x.Name == LocalRepositoryAlias) && configuration.DefaultLocalRepository is not null) aliases.Add(new RepositoryAlias { Name = LocalRepositoryAlias, Address = configuration.DefaultLocalRepository });
        if (!aliases.Any(x => x.Name == RemoteRepositoryAlias) && configuration.DefaultServer is not null) aliases.Add(new RepositoryAlias { Name = RemoteRepositoryAlias, Address = configuration.DefaultServer });
        if (aliases.Any(x => x.Name == ReservedDefaultAlias)) throw new FormatException($"The alias '{ReservedDefaultAlias}' is reserved.");
        return configuration with { Aliases = aliases.ToImmutable() };
    }

    /// <summary>Writes one setting to the highest-precedence owning layer.</summary>
    /// <param name="key">Configuration property name.</param>
    /// <param name="value">JSON value to store.</param>
    /// <param name="cancellationToken">Token used to cancel asynchronous file access.</param>
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
