using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace FourSaas.AutoUpdater.Core;

public sealed record SignedSignature
{
    public required string KeyId { get; init; }
    public required string Algorithm { get; init; }
    public required string Signature { get; init; }
}

public sealed record SignedEnvelope
{
    public required int Envelope { get; init; }
    public required string Type { get; init; }
    public required string Payload { get; init; }
    public required ImmutableArray<SignedSignature> Signatures { get; init; }
}

public static class SignedDocument
{
    public static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly ImmutableHashSet<string> KnownTypes = ["channel-pointer", "release-lock", "key-manifest", "revocation"];

    public static byte[] SigningInput(string type, ReadOnlySpan<byte> payload) =>
        Encoding.UTF8.GetBytes("4sup-v1\0" + type + "\0").Concat(payload.ToArray()).ToArray();

    public static SignedEnvelope Sign(string type, ReadOnlySpan<byte> payload, string keyId, ReadOnlySpan<byte> privateKey)
    {
        if (!KnownTypes.Contains(type)) throw new ArgumentException($"Unknown signed document type '{type}'.", nameof(type));
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(privateKey.ToArray(), 0));
        var input = SigningInput(type, payload);
        signer.BlockUpdate(input, 0, input.Length);
        var signature = signer.GenerateSignature();
        return new SignedEnvelope { Envelope = 1, Type = type, Payload = Base64Url.Encode(payload), Signatures = [new SignedSignature { KeyId = keyId, Algorithm = "ed25519", Signature = Base64Url.Encode(signature) }] };
    }

    public static bool Verify(SignedEnvelope envelope, IReadOnlyDictionary<string, byte[]> trustedKeys, out byte[] payload, out string? error)
    {
        payload = [];
        error = null;
        if (envelope.Envelope != 1) { error = "Unknown signed-envelope version."; return false; }
        if (!KnownTypes.Contains(envelope.Type)) { error = "Unknown signed-envelope type."; return false; }
        try { payload = Base64Url.Decode(envelope.Payload); }
        catch (FormatException ex) { error = ex.Message; return false; }
        try { JsonRules.Validate(payload); }
        catch (FormatException ex) { error = ex.Message; return false; }
        var input = SigningInput(envelope.Type, payload);
        foreach (var signature in envelope.Signatures)
        {
            if (!string.Equals(signature.Algorithm, "ed25519", StringComparison.OrdinalIgnoreCase) || !trustedKeys.TryGetValue(signature.KeyId, out var publicKey)) continue;
            byte[] bytes;
            try { bytes = Base64Url.Decode(signature.Signature); } catch (FormatException) { continue; }
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
            verifier.BlockUpdate(input, 0, input.Length);
            if (verifier.VerifySignature(bytes)) return true;
        }
        error = "No signature matched a trusted Ed25519 key.";
        return false;
    }

    public static byte[] SerializeEnvelope(SignedEnvelope envelope) => JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
    public static SignedEnvelope DeserializeEnvelope(ReadOnlySpan<byte> bytes)
    {
        JsonRules.Validate(bytes);
        var options = new JsonSerializerOptions(JsonOptions) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var envelope = JsonSerializer.Deserialize<SignedEnvelope>(bytes, options) ?? throw new FormatException("Signed envelope is empty.");
        if (envelope.Envelope != 1 || envelope.Signatures.IsDefaultOrEmpty || !KnownTypes.Contains(envelope.Type)) throw new FormatException("Signed envelope is missing required fields or has an unknown type.");
        return envelope;
    }

    public static byte[] SerializePayload<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
    public static T DeserializePayload<T>(ReadOnlySpan<byte> bytes)
    {
        JsonRules.Validate(bytes);
        var options = new JsonSerializerOptions(JsonOptions) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        return JsonSerializer.Deserialize<T>(bytes, options) ?? throw new FormatException("Signed payload is empty.");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new ContentHashJsonConverter(), new PackageIdJsonConverter(), new PackageVersionJsonConverter(), new VirtualPathJsonConverter(), new UtcSecondJsonConverter(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
        return options;
    }
}

public static class JsonRules
{
    public static void Validate(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        var stack = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject: stack.Push(new HashSet<string>(StringComparer.Ordinal)); break;
                case JsonTokenType.PropertyName: if (stack.Count == 0 || !stack.Peek().Add(reader.GetString() ?? string.Empty)) throw new FormatException("Duplicate JSON object property."); break;
                case JsonTokenType.Null: throw new FormatException("Null is not valid in a signed document.");
                case JsonTokenType.Number: if (!reader.TryGetInt64(out _)) throw new FormatException("Signed-document numbers must be int64 integers."); break;
                case JsonTokenType.String: ValidateString(reader.GetString()); break;
                case JsonTokenType.EndObject: if (stack.Count > 0) stack.Pop(); break;
            }
        }
        if (reader.BytesConsumed != bytes.Length) throw new FormatException("Trailing bytes after signed JSON document.");
    }

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

public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] Decode(string text)
    {
        if (text.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_'))) throw new FormatException("Invalid base64url text.");
        var padded = text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}

public sealed class ContentHashJsonConverter : JsonConverter<ContentHash>
{
    public override ContentHash Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => ContentHash.Parse(reader.GetString() ?? throw new JsonException("Hash cannot be null."));
    public override void Write(Utf8JsonWriter writer, ContentHash value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
public sealed class PackageIdJsonConverter : JsonConverter<PackageId>
{
    public override PackageId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString() ?? throw new JsonException("Package id cannot be null."));
    public override void Write(Utf8JsonWriter writer, PackageId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
    public override PackageId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString() ?? throw new JsonException("Package id property cannot be null."));
    public override void WriteAsPropertyName(Utf8JsonWriter writer, PackageId value, JsonSerializerOptions options) => writer.WritePropertyName(value.Value);
}
public sealed class PackageVersionJsonConverter : JsonConverter<PackageVersion>
{
    public override PackageVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString() ?? throw new JsonException("Version cannot be null."), 0);
    public override void Write(Utf8JsonWriter writer, PackageVersion value, JsonSerializerOptions options) => writer.WriteStringValue(value.Label);
}
public sealed class VirtualPathJsonConverter : JsonConverter<VirtualPath>
{
    public override VirtualPath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString() ?? throw new JsonException("Path cannot be null."));
    public override void Write(Utf8JsonWriter writer, VirtualPath value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
}

public sealed class UtcSecondJsonConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (value is null || !DateTimeOffset.TryParseExact(value, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            throw new JsonException("Timestamps must be RFC 3339 UTC with second precision and a literal Z.");
        return parsed;
    }
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) => writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
}
