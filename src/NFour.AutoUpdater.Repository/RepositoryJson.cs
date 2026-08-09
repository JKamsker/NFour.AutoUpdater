namespace NFour.AutoUpdater.Repository;

/// <summary>Serializes and validates canonical repository JSON documents.</summary>
public static class RepositoryJson
{
    /// <summary>Gets the canonical repository serializer settings.</summary>
    public static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>Serializes a repository value to UTF-8 JSON.</summary>
    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    /// <summary>Validates and deserializes UTF-8 repository JSON.</summary>
    public static T Deserialize<T>(ReadOnlySpan<byte> bytes, bool rejectUnknownFields = false)
    {
        JsonSafety.Validate(bytes);
        var options = new JsonSerializerOptions(Options) { UnmappedMemberHandling = rejectUnknownFields ? JsonUnmappedMemberHandling.Disallow : JsonUnmappedMemberHandling.Skip };
        return JsonSerializer.Deserialize<T>(bytes, options) ?? throw new FormatException("JSON document is empty.");
    }

    /// <summary>Validates and deserializes a package manifest.</summary>
    public static PackageManifest DeserializeManifest(ReadOnlySpan<byte> bytes)
    {
        var dto = Deserialize<PackageManifestDocument>(bytes, rejectUnknownFields: true);
        if (dto.SchemaVersion != 1) throw new FormatException($"Unsupported package schemaVersion {dto.SchemaVersion}.");
        if (!PackageId.TryCreate(dto.Id, out var id)) throw new FormatException("Manifest contains an invalid package id.");
        if (!Identifier.IsValid(dto.Version, 64)) throw new FormatException("Manifest contains an invalid package version.");
        if (dto.Sequence < 0) throw new FormatException("Manifest sequence cannot be negative.");
        if (dto.FileTable.Format != "jsonl/v1") throw new FormatException($"Unsupported file table format '{dto.FileTable.Format}'.");
        if (dto.FileTable.ShardCount < 1 || dto.FileTable.ShardCount > 1_048_576 ||
            (dto.FileTable.ShardCount & (dto.FileTable.ShardCount - 1)) != 0 ||
            dto.FileTable.Shards.Length != dto.FileTable.ShardCount)
            throw new FormatException("File-table shardCount must be a positive power of two matching the shard list.");
        var version = new PackageVersion(dto.Version, dto.Sequence);
        var fileTable = new FileTableRef
        {
            Format = dto.FileTable.Format,
            ShardCount = dto.FileTable.ShardCount,
            Digest = ContentHash.Parse(dto.FileTable.Digest),
            Shards = dto.FileTable.Shards.Select(x => new FileTableShardRef { Index = x.Index, Digest = ContentHash.Parse(x.Digest), Count = x.Count, Size = x.Size }).ToImmutableArray()
        };
        if (fileTable.Shards.Select(x => x.Index).OrderBy(x => x).SequenceEqual(Enumerable.Range(0, fileTable.ShardCount)) is false)
            throw new FormatException("File-table shard indexes must be contiguous from zero.");
        if (fileTable.Digest.Algorithm != HashAlgorithmId.Sha256 || fileTable.Shards.Any(x => x.Digest.Algorithm != HashAlgorithmId.Sha256))
            throw new FormatException("Package file-table digests must use sha256.");
        if (dto.FileCount < 0 || dto.InstallSize < 0 || dto.DownloadSize < 0)
            throw new FormatException("Package sizes and fileCount cannot be negative.");
        return new PackageManifest
        {
            SchemaVersion = dto.SchemaVersion,
            Id = id,
            Version = version,
            Sequence = dto.Sequence,
            Kind = dto.Kind,
            CreatedAt = dto.CreatedAt,
            Conflicts = dto.Conflicts.Select(x => PackageId.TryCreate(x, out var conflict) ? conflict : throw new FormatException($"Invalid conflicting package id '{x}'.")).ToImmutableArray(),
            Requires = dto.Requires.Select(x => PackageId.TryCreate(x.Id, out var dependency) ? new PackageDependency { Id = dependency, MinSequence = x.MinSequence, MaxSequence = x.MaxSequence } : throw new FormatException($"Invalid dependency package id '{x.Id}'.")).ToImmutableArray(),
            PathPrefixes = dto.PathPrefixes,
            FileTable = fileTable,
            FileCount = dto.FileCount,
            InstallSize = dto.InstallSize,
            DownloadSize = dto.DownloadSize,
            Metadata = dto.Metadata
        };
    }

    /// <summary>Serializes a package manifest using its wire-format version representation.</summary>
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

/// <summary>Validates repository JSON safety and canonical-value constraints.</summary>
public static class JsonSafety
{
    /// <summary>Rejects duplicate properties, nulls, non-integer numbers, and trailing data.</summary>
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
