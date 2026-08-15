namespace NFour.AutoUpdater.Core;

/// <summary>Converts revocation effects to and from their wire-format identifiers.</summary>
public sealed class RevocationEffectJsonConverter : JsonConverter<RevocationEffect>
{
    /// <inheritdoc />
    public override RevocationEffect Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value switch
        {
            "block-install" => RevocationEffect.BlockInstall,
            "block-repair" => RevocationEffect.BlockRepair,
            "force-move" => RevocationEffect.ForceMove,
            _ => throw new JsonException($"Unknown revocation effect '{value}'.")
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, RevocationEffect value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        RevocationEffect.BlockInstall => "block-install",
        RevocationEffect.BlockRepair => "block-repair",
        RevocationEffect.ForceMove => "force-move",
        _ => throw new JsonException($"Unknown revocation effect '{value}'.")
    });
}

/// <summary>Validates the strict JSON subset accepted for signed updater documents.</summary>
public static class JsonRules
{
    /// <summary>Validates UTF-8 JSON for duplicate properties, trailing data, and forbidden forms.</summary>
    /// <param name="bytes">The UTF-8 JSON bytes.</param>
    public static void Validate(ReadOnlySpan<byte> bytes)
    {
        var hasNonWhitespace = false;
        foreach (var value in bytes)
            if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) { hasNonWhitespace = true; break; }
        if (!hasNonWhitespace) throw new FormatException("Signed JSON must not be empty.");
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        var stack = new Stack<HashSet<string>>();
        var sawToken = false;
        while (reader.Read())
        {
            sawToken = true;
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject: stack.Push(new HashSet<string>(StringComparer.Ordinal)); break;
                case JsonTokenType.PropertyName:
                    var propertyName = reader.GetString() ?? throw new FormatException("JSON property names may not be null.");
                    ValidateString(propertyName);
                    if (stack.Count == 0 || !stack.Peek().Add(propertyName)) throw new FormatException("Duplicate JSON object property.");
                    break;
                case JsonTokenType.Null: throw new FormatException("Null is not valid in a signed document.");
                case JsonTokenType.Number: if (!reader.TryGetInt64(out _)) throw new FormatException("Signed-document numbers must be int64 integers."); break;
                case JsonTokenType.String: ValidateString(reader.GetString()); break;
                case JsonTokenType.EndObject: if (stack.Count > 0) stack.Pop(); break;
            }
        }
        if (!sawToken || stack.Count != 0) throw new FormatException("Signed JSON is incomplete.");
        if (reader.BytesConsumed != bytes.Length) throw new FormatException("Trailing bytes after signed JSON document.");
    }

    /// <summary>Validates that a JSON string is normalized and contains no forbidden code points.</summary>
    /// <param name="text">The string to validate.</param>
    public static void ValidateString(string? text)
    {
        if (text is null) throw new FormatException("JSON strings may not be null.");
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]) && (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))) throw new FormatException("JSON strings may not contain lone surrogates.");
            if (char.IsLowSurrogate(text[index]) && (index == 0 || !char.IsHighSurrogate(text[index - 1]))) throw new FormatException("JSON strings may not contain lone surrogates.");
        }
        if (text.Normalize(NormalizationForm.FormC) != text) throw new FormatException("JSON strings must be Unicode NFC.");
    }
}

/// <summary>Encodes and decodes unpadded base64url values.</summary>
public static class Base64Url
{
    /// <summary>Encodes bytes as canonical unpadded base64url text.</summary>
    /// <param name="bytes">The bytes to encode.</param>
    /// <returns>The encoded text.</returns>
    public static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    /// <summary>Decodes canonical unpadded base64url text.</summary>
    /// <param name="text">The text to decode.</param>
    /// <returns>The decoded bytes.</returns>
    public static byte[] Decode(string text)
    {
        if (text.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_'))) throw new FormatException("Invalid base64url text.");
        var padded = text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}

/// <summary>Converts content hashes to and from canonical string values.</summary>
public sealed class ContentHashJsonConverter : JsonConverter<ContentHash>
{
    /// <inheritdoc />
    public override ContentHash Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => ContentHash.Parse(reader.GetString() ?? throw new JsonException("Hash cannot be null."));
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ContentHash value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
/// <summary>Converts package identifiers to and from canonical string values and property names.</summary>
public sealed class PackageIdJsonConverter : JsonConverter<PackageId>
{
    /// <inheritdoc />
    public override PackageId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString() ?? throw new JsonException("Package id cannot be null."));
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, PackageId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
    /// <inheritdoc />
    public override PackageId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString() ?? throw new JsonException("Package id property cannot be null."));
    /// <inheritdoc />
    public override void WriteAsPropertyName(Utf8JsonWriter writer, PackageId value, JsonSerializerOptions options) => writer.WritePropertyName(value.Value);
}
/// <summary>Converts package versions to and from their version labels.</summary>
public sealed class PackageVersionJsonConverter : JsonConverter<PackageVersion>
{
    /// <inheritdoc />
    public override PackageVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString() ?? throw new JsonException("Version cannot be null."), 0);
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, PackageVersion value, JsonSerializerOptions options) => writer.WriteStringValue(value.Label);
}
/// <summary>Converts virtual paths to and from canonical slash-separated strings.</summary>
public sealed class VirtualPathJsonConverter : JsonConverter<VirtualPath>
{
    /// <inheritdoc />
    public override VirtualPath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString() ?? throw new JsonException("Path cannot be null."));
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, VirtualPath value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
}

/// <summary>Converts timestamps to and from canonical UTC second-precision strings.</summary>
public sealed class UtcSecondJsonConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    /// <inheritdoc />
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (value is null || !DateTimeOffset.TryParseExact(value, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            throw new JsonException("Timestamps must be RFC 3339 UTC with second precision and a literal Z.");
        return parsed;
    }
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) => writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
}

