namespace FourSaas.AutoUpdater.Repository;

public static class RepositoryJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T Deserialize<T>(ReadOnlySpan<byte> bytes, bool rejectUnknownFields = false)
    {
        JsonSafety.Validate(bytes);
        var options = new JsonSerializerOptions(Options) { UnmappedMemberHandling = rejectUnknownFields ? JsonUnmappedMemberHandling.Disallow : JsonUnmappedMemberHandling.Skip };
        return JsonSerializer.Deserialize<T>(bytes, options) ?? throw new FormatException("JSON document is empty.");
    }

    public static PackageManifest DeserializeManifest(ReadOnlySpan<byte> bytes)
    {
        var dto = Deserialize<PackageManifestDocument>(bytes, rejectUnknownFields: true);
        if (!PackageId.TryCreate(dto.Id, out var id)) throw new FormatException("Manifest contains an invalid package id.");
        if (!Identifier.IsValid(dto.Version, 64)) throw new FormatException("Manifest contains an invalid package version.");
        var version = new PackageVersion(dto.Version, dto.Sequence);
        var fileTable = new FileTableRef
        {
            Format = dto.FileTable.Format,
            ShardCount = dto.FileTable.ShardCount,
            Digest = ContentHash.Parse(dto.FileTable.Digest),
            Shards = dto.FileTable.Shards.Select(x => new FileTableShardRef { Index = x.Index, Digest = ContentHash.Parse(x.Digest), Count = x.Count, Size = x.Size }).ToImmutableArray()
        };
        return new PackageManifest
        {
            SchemaVersion = dto.SchemaVersion,
            Id = id,
            Version = version,
            Sequence = dto.Sequence,
            Kind = dto.Kind,
            CreatedAt = dto.CreatedAt,
            Conflicts = dto.Conflicts.Select(x => new PackageId(x)).ToImmutableArray(),
            Requires = dto.Requires.Select(x => new PackageDependency { Id = new PackageId(x.Id), MinSequence = x.MinSequence, MaxSequence = x.MaxSequence }).ToImmutableArray(),
            PathPrefixes = dto.PathPrefixes,
            FileTable = fileTable,
            FileCount = dto.FileCount,
            InstallSize = dto.InstallSize,
            DownloadSize = dto.DownloadSize,
            Metadata = dto.Metadata
        };
    }

    public static byte[] SerializeManifest(PackageManifest value)
    {
        var dto = new PackageManifestDocument
        {
            SchemaVersion = value.SchemaVersion, Id = value.Id.Value, Version = value.Version.Label, Sequence = value.Sequence == 0 ? value.Version.Sequence : value.Sequence,
            Kind = value.Kind, CreatedAt = value.CreatedAt, Conflicts = value.Conflicts.Select(x => x.Value).ToImmutableArray(),
            Requires = value.Requires.Select(x => new DependencyDocument { Id = x.Id.Value, MinSequence = x.MinSequence, MaxSequence = x.MaxSequence }).ToImmutableArray(), PathPrefixes = value.PathPrefixes,
            FileTable = new FileTableDocument { Format = value.FileTable.Format, ShardCount = value.FileTable.ShardCount, Digest = value.FileTable.Digest.ToString(), Shards = value.FileTable.Shards.Select(x => new ShardDocument { Index = x.Index, Digest = x.Digest.ToString(), Count = x.Count, Size = x.Size }).ToImmutableArray() },
            FileCount = value.FileCount, InstallSize = value.InstallSize, DownloadSize = value.DownloadSize, Metadata = value.Metadata
        };
        return Serialize(dto);
    }

    private static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new ContentHashJsonConverter(), new PackageIdJsonConverter(), new PackageVersionJsonConverter(), new VirtualPathJsonConverter(), new UtcSecondJsonConverter(), new StorageCapabilitiesJsonConverter(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private sealed record PackageManifestDocument
    {
        public required int SchemaVersion { get; init; }
        public required string Id { get; init; }
        public required string Version { get; init; }
        public required long Sequence { get; init; }
        public required PackageKind Kind { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public ImmutableArray<string> Conflicts { get; init; } = [];
        public ImmutableArray<DependencyDocument> Requires { get; init; } = [];
        public required ImmutableArray<string> PathPrefixes { get; init; }
        public required FileTableDocument FileTable { get; init; }
        public required int FileCount { get; init; }
        public required long InstallSize { get; init; }
        public required long DownloadSize { get; init; }
        public ImmutableDictionary<string, JsonElement>? Metadata { get; init; }
    }
    private sealed record DependencyDocument { public required string Id { get; init; } public long? MinSequence { get; init; } public long? MaxSequence { get; init; } }
    private sealed record FileTableDocument { public required string Format { get; init; } public required int ShardCount { get; init; } public required string Digest { get; init; } public required ImmutableArray<ShardDocument> Shards { get; init; } }
    private sealed record ShardDocument { public required int Index { get; init; } public required string Digest { get; init; } public required int Count { get; init; } public required long Size { get; init; } }
}

public static class JsonSafety
{
    public static void Validate(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        var stack = new Stack<HashSet<string>>(8);
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject: stack.Push(new HashSet<string>(StringComparer.Ordinal)); break;
                case JsonTokenType.PropertyName:
                    if (stack.Count == 0 || !stack.Peek().Add(reader.GetString() ?? throw new FormatException("JSON property name is null."))) throw new FormatException("Duplicate JSON object property.");
                    break;
                case JsonTokenType.Null: throw new FormatException("Null is not valid in the repository format; omit optional fields instead.");
                case JsonTokenType.Number:
                    if (!reader.TryGetInt64(out _)) throw new FormatException("JSON numbers must be integers in int64 range.");
                    break;
                case JsonTokenType.String:
                    JsonRules.ValidateString(reader.GetString());
                    break;
                case JsonTokenType.EndObject: if (stack.Count > 0) stack.Pop(); break;
            }
        }
        if (reader.BytesConsumed != bytes.Length) throw new FormatException("Trailing data after JSON document.");
    }
}
