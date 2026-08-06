namespace FourSaas.AutoUpdater.Core;

/// <summary>
/// Trust anchors compiled into the first-party client.  Repository metadata may
/// select among these keys, but it can never introduce a new bootstrap key.
/// The sample distribution ships the development anchor below; a first-party
/// build replaces this value at build time with its offline-controlled root.
/// </summary>
public static class CompiledTrustRoots
{
    public const string FirstPartyKeyId = "4s-2026";
    private const string FirstPartyPublicKey = "A6EHv_POEL4dcN0Y50vAmWfk1jCbpQ1fHdyGZBJVMbg";

    public static IReadOnlyDictionary<string, byte[]> FirstParty
        => new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [FirstPartyKeyId] = Base64Url.Decode(FirstPartyPublicKey)
        };
}
