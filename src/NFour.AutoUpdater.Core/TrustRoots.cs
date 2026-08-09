namespace NFour.AutoUpdater.Core;

/// <summary>
/// Trust anchors compiled into the first-party client.  Repository metadata may
/// select among these keys, but it can never introduce a new bootstrap key.
///
/// The values come from the <c>NFourTrustRootKeyId</c> and
/// <c>NFourTrustRootPublicKey</c> MSBuild properties, which default to the development
/// anchor below.  A Release build that still carries the development anchor fails at build
/// time (see <c>TrustRoot.targets</c>), because a client shipped with it would trust the
/// development-key ecosystem and accept releases signed by anyone holding that publicly
/// known key.  Set both properties, or pass
/// <c>-p:NFourAllowDevelopmentTrustRoot=true</c> to acknowledge a non-shippable build.
/// </summary>
public static class CompiledTrustRoots
{
    /// <summary>Gets the identifier of the non-production development trust anchor.</summary>
    public const string DevelopmentKeyId = "4s-dev-0000";
    /// <summary>Gets the encoded public key of the non-production development trust anchor.</summary>
    public const string DevelopmentPublicKey = "A6EHv_POEL4dcN0Y50vAmWfk1jCbpQ1fHdyGZBJVMbg";

    /// <summary>Gets the identifier of the trust anchor compiled into this assembly.</summary>
    public const string FirstPartyKeyId = TrustRootBuildConfiguration.KeyId;
    private const string FirstPartyPublicKey = TrustRootBuildConfiguration.PublicKey;

    /// <summary>True when this binary was built with the development anchor still in place.</summary>
    public static bool IsDevelopmentTrustRoot
        => string.Equals(FirstPartyKeyId, DevelopmentKeyId, StringComparison.Ordinal)
        || string.Equals(FirstPartyPublicKey, DevelopmentPublicKey, StringComparison.Ordinal);

    /// <summary>Gets the compiled bootstrap verification keys keyed by identifier.</summary>
    public static IReadOnlyDictionary<string, byte[]> FirstParty
        => new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [FirstPartyKeyId] = Base64Url.Decode(FirstPartyPublicKey)
        };
}
