using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace NFour.AutoUpdater.Core;

/// <summary>Describes one detached signature attached to a signed envelope.</summary>
public sealed record SignedSignature
{
    /// <summary>Gets the signing-key identifier.</summary>
    public required string KeyId { get; init; }
    /// <summary>Gets the signature algorithm identifier.</summary>
    public required string Algorithm { get; init; }
    /// <summary>Gets the base64url-encoded signature bytes.</summary>
    public required string Signature { get; init; }
}
/// <summary>Contains typed payload bytes and one or more detached signatures.</summary>
public sealed record SignedEnvelope
{
    /// <summary>Gets the envelope format version.</summary>
    public required int Envelope { get; init; }
    /// <summary>Gets the signed document type.</summary>
    public required string Type { get; init; }
    /// <summary>Gets the base64url-encoded exact payload bytes.</summary>
    public required string Payload { get; init; }
    /// <summary>Gets the signatures over the payload.</summary>
    public required ImmutableArray<SignedSignature> Signatures { get; init; }
}

/// <summary>Contains an Ed25519 verification key and its optional validity window.</summary>
/// <param name="PublicKey">The public verification-key bytes.</param>
/// <param name="NotBefore">The beginning of the validity window.</param>
/// <param name="NotAfter">The end of the validity window.</param>
public sealed record VerificationKey(byte[] PublicKey, DateTimeOffset? NotBefore = null, DateTimeOffset? NotAfter = null)
{
    private const int Ed25519PublicKeyLength = 32;

    /// <summary>Determines whether the key is well formed and valid at a time.</summary>
    /// <param name="now">The evaluation time.</param>
    /// <returns><see langword="true"/> when the key may verify a document at that time.</returns>
    public bool IsValidAt(DateTimeOffset now) => PublicKey.Length == Ed25519PublicKeyLength && (NotBefore is null || now >= NotBefore) && (NotAfter is null || now <= NotAfter);
}

/// <summary>Creates, verifies, and serializes signed updater control documents.</summary>
public static class SignedDocument
{
    private const int CurrentEnvelopeVersion = 1;
    private const int Ed25519PublicKeyLength = 32;
    private const string Ed25519Algorithm = "ed25519";
    private const string SigningDomainPrefix = "4sup-v1\0";

    /// <summary>Gets the canonical serializer settings used for signed documents.</summary>
    public static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly ImmutableHashSet<string> KnownTypes = ["channel-pointer", "release-lock", "key-manifest", "revocation"];

    /// <summary>Builds the domain-separated bytes covered by a document signature.</summary>
    /// <param name="type">The signed document type.</param>
    /// <param name="payload">The exact payload bytes.</param>
    /// <returns>The bytes passed to the signature algorithm.</returns>
    public static byte[] SigningInput(string type, ReadOnlySpan<byte> payload) =>
        Encoding.UTF8.GetBytes(SigningDomainPrefix + type + "\0").Concat(payload.ToArray()).ToArray();

    /// <summary>Signs exact payload bytes and creates a versioned envelope.</summary>
    /// <param name="type">The registered document type.</param>
    /// <param name="payload">The exact payload bytes.</param>
    /// <param name="keyId">The signing-key identifier.</param>
    /// <param name="privateKey">The Ed25519 private-key bytes.</param>
    /// <returns>The signed envelope.</returns>
    public static SignedEnvelope Sign(string type, ReadOnlySpan<byte> payload, string keyId, ReadOnlySpan<byte> privateKey)
    {
        if (!KnownTypes.Contains(type)) throw new ArgumentException($"Unknown signed document type '{type}'.", nameof(type));
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(privateKey.ToArray(), 0));
        var input = SigningInput(type, payload);
        signer.BlockUpdate(input, 0, input.Length);
        var signature = signer.GenerateSignature();
        return new SignedEnvelope { Envelope = CurrentEnvelopeVersion, Type = type, Payload = Base64Url.Encode(payload), Signatures = [new SignedSignature { KeyId = keyId, Algorithm = Ed25519Algorithm, Signature = Base64Url.Encode(signature) }] };
    }

    /// <summary>Verifies an envelope against raw trusted Ed25519 keys.</summary>
    /// <param name="envelope">The envelope to verify.</param>
    /// <param name="trustedKeys">Trusted public keys keyed by identifier.</param>
    /// <param name="payload">Receives the verified exact payload bytes.</param>
    /// <param name="error">Receives the rejection reason.</param>
    /// <returns><see langword="true"/> when a trusted signature and the payload are valid.</returns>
    public static bool Verify(SignedEnvelope envelope, IReadOnlyDictionary<string, byte[]> trustedKeys, out byte[] payload, out string? error)
    {
        payload = [];
        error = null;
        if (envelope.Envelope != CurrentEnvelopeVersion) { error = "Unknown signed-envelope version."; return false; }
        if (!KnownTypes.Contains(envelope.Type)) { error = "Unknown signed-envelope type."; return false; }
        try { payload = Base64Url.Decode(envelope.Payload); }
        catch (FormatException ex) { error = ex.Message; return false; }
        var input = SigningInput(envelope.Type, payload);
        foreach (var signature in envelope.Signatures)
        {
            if (!string.Equals(signature.Algorithm, Ed25519Algorithm, StringComparison.Ordinal) || !trustedKeys.TryGetValue(signature.KeyId, out var publicKey)) continue;
            byte[] bytes;
            try { bytes = Base64Url.Decode(signature.Signature); } catch (FormatException) { continue; }
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
            verifier.BlockUpdate(input, 0, input.Length);
            if (!verifier.VerifySignature(bytes)) continue;
            try { JsonRules.Validate(payload); }
            catch (FormatException ex) { error = ex.Message; return false; }
            catch (JsonException ex) { error = ex.Message; return false; }
            return true;
        }
        error = "No signature matched a trusted Ed25519 key.";
        return false;
    }

    /// <summary>Verifies an envelope against time-bounded trusted keys.</summary>
    /// <param name="envelope">The envelope to verify.</param>
    /// <param name="trustedKeys">Trusted verification keys keyed by identifier.</param>
    /// <param name="payload">Receives the verified exact payload bytes.</param>
    /// <param name="error">Receives the rejection reason.</param>
    /// <param name="now">The evaluation time, or UTC now when omitted.</param>
    /// <returns><see langword="true"/> when a currently valid trusted key verifies the payload.</returns>
    public static bool Verify(SignedEnvelope envelope, IReadOnlyDictionary<string, VerificationKey> trustedKeys, out byte[] payload, out string? error, DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        var valid = trustedKeys.Where(x => x.Value.IsValidAt(current)).ToDictionary(x => x.Key, x => x.Value.PublicKey, StringComparer.Ordinal);
        if (valid.Count == 0)
        {
            payload = [];
            error = "No trusted signing key is currently within its validity window.";
            return false;
        }
        return Verify(envelope, valid, out payload, out error);
    }

    /// <summary>Verifies the signature while also returning the signer, without applying a
    /// validity window. Callers that learn the signed document's timestamp after parsing must
    /// use <see cref="IsKeyValidAt"/> with that timestamp before accepting the document.</summary>
    public static bool VerifyCryptographically(SignedEnvelope envelope, IReadOnlyDictionary<string, VerificationKey> trustedKeys, out byte[] payload, out string? signingKeyId, out string? error, Func<string, bool>? signerAllowed = null)
    {
        payload = [];
        signingKeyId = null;
        error = null;
        if (envelope.Envelope != CurrentEnvelopeVersion || !KnownTypes.Contains(envelope.Type)) { error = "Unknown signed-envelope version or type."; return false; }
        try { payload = Base64Url.Decode(envelope.Payload); }
        catch (FormatException ex) { error = ex.Message; return false; }
        var input = SigningInput(envelope.Type, payload);
        foreach (var signature in envelope.Signatures)
        {
            if (!string.Equals(signature.Algorithm, Ed25519Algorithm, StringComparison.Ordinal) || !trustedKeys.TryGetValue(signature.KeyId, out var key) || key.PublicKey.Length != Ed25519PublicKeyLength) continue;
            byte[] bytes;
            try { bytes = Base64Url.Decode(signature.Signature); } catch (FormatException) { continue; }
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(key.PublicKey, 0));
            verifier.BlockUpdate(input, 0, input.Length);
            if (!verifier.VerifySignature(bytes)) continue;
            if (signerAllowed is not null && !signerAllowed(signature.KeyId)) continue;
            try { JsonRules.Validate(payload); }
            catch (Exception ex) when (ex is FormatException or JsonException) { error = ex.Message; return false; }
            signingKeyId = signature.KeyId;
            return true;
        }
        error = "No signature matched a trusted Ed25519 key.";
        return false;
    }

    /// <summary>Checks whether the signing key was trusted at the document timestamp.</summary>
    /// <param name="trustedKeys">Trusted verification keys keyed by identifier.</param>
    /// <param name="signingKeyId">The identifier returned by cryptographic verification.</param>
    /// <param name="at">The signed document timestamp.</param>
    /// <param name="error">Receives the rejection reason.</param>
    /// <param name="clockSkew">The permitted clock skew.</param>
    /// <returns><see langword="true"/> when the key was valid at the timestamp.</returns>
    public static bool IsKeyValidAt(IReadOnlyDictionary<string, VerificationKey> trustedKeys, string signingKeyId, DateTimeOffset at, out string? error, TimeSpan? clockSkew = null)
    {
        if (!trustedKeys.TryGetValue(signingKeyId, out var key)) { error = $"Signing key '{signingKeyId}' is not trusted."; return false; }
        var skew = clockSkew ?? TimeSpan.Zero;
        if (key.NotBefore is { } notBefore && notBefore > at + skew || key.NotAfter is { } notAfter && notAfter < at - skew)
        { error = $"Signing key '{signingKeyId}' was not valid at {at:O} (clock skew {skew})."; return false; }
        if (key.PublicKey.Length != Ed25519PublicKeyLength) { error = $"Signing key '{signingKeyId}' is not a valid Ed25519 public key."; return false; }
        error = null;
        return true;
    }

    /// <summary>Serializes a signed envelope with canonical updater JSON settings.</summary>
    /// <param name="envelope">The envelope to serialize.</param>
    /// <returns>The UTF-8 JSON bytes.</returns>
    public static byte[] SerializeEnvelope(SignedEnvelope envelope) => JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
    /// <summary>Validates and deserializes a signed envelope.</summary>
    /// <param name="bytes">The UTF-8 JSON bytes.</param>
    /// <returns>The parsed envelope.</returns>
    public static SignedEnvelope DeserializeEnvelope(ReadOnlySpan<byte> bytes)
    {
        JsonRules.Validate(bytes);
        var options = new JsonSerializerOptions(JsonOptions) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var envelope = JsonSerializer.Deserialize<SignedEnvelope>(bytes, options) ?? throw new FormatException("Signed envelope is empty.");
        if (envelope.Envelope != CurrentEnvelopeVersion || envelope.Signatures.IsDefaultOrEmpty || !KnownTypes.Contains(envelope.Type)) throw new FormatException("Signed envelope is missing required fields or has an unknown type.");
        return envelope;
    }

    /// <summary>Serializes a signed-document payload with canonical updater JSON settings.</summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="value">The payload value.</param>
    /// <returns>The UTF-8 JSON bytes.</returns>
    public static byte[] SerializePayload<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
    /// <summary>Validates and deserializes signed-document payload bytes.</summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="bytes">The UTF-8 JSON bytes.</param>
    /// <returns>The parsed payload.</returns>
    public static T DeserializePayload<T>(ReadOnlySpan<byte> bytes)
    {
        JsonRules.Validate(bytes);
        var options = new JsonSerializerOptions(JsonOptions) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        return JsonSerializer.Deserialize<T>(bytes, options) ?? throw new FormatException("Signed payload is empty.");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new ContentHashJsonConverter(), new PackageIdJsonConverter(), new PackageVersionJsonConverter(), new VirtualPathJsonConverter(), new UtcSecondJsonConverter(), new RevocationEffectJsonConverter(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
        return options;
    }
}
