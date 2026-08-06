using System.Security.Cryptography;
using System.Text;

namespace FourSaas.AutoUpdater.Server;

/// <summary>
/// Comparison of a presented bearer token against a configured static token.
///
/// Extracted from the request pipeline so the rules below are directly testable rather than
/// only reachable through a hosted server.
/// </summary>
public static class StaticTokenAuthentication
{
    /// <summary>
    /// True when <paramref name="presented"/> is a bearer header carrying exactly
    /// <paramref name="expected"/>.
    /// </summary>
    /// <remarks>
    /// An unset or blank expected token never matches. The previous inline check built its
    /// comparison target as "Bearer " + token, so with the token variable unset the target
    /// collapsed to the literal "Bearer " and a request sending exactly that authenticated
    /// successfully.
    ///
    /// The comparison runs over fixed-size digests in constant time so response timing does
    /// not reveal how much of a guessed token was correct.
    /// </remarks>
    public static bool Matches(string? presented, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return false;
        if (presented is null) return false;
        const string Scheme = "Bearer ";
        if (!presented.StartsWith(Scheme, StringComparison.Ordinal)) return false;

        var presentedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(presented[Scheme.Length..]));
        var expectedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(presentedDigest, expectedDigest);
    }

    /// <summary>
    /// Stable, non-secret identifier for a presented credential, used so audit records name a
    /// principal rather than only the role it happened to satisfy.
    /// </summary>
    public static string SubjectFingerprint(string presented)
        => "static:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(presented))).ToLowerInvariant()[..12];
}
