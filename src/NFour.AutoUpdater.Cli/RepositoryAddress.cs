using NFour.AutoUpdater.Core;

namespace NFour.AutoUpdater.Cli;

/// <summary>Identifies a product and release reference within a repository backend.</summary>
/// <param name="Backend">Storage backend name.</param>
/// <param name="BaseUri">Repository base URI.</param>
/// <param name="ProductId">Product identifier below the repository root.</param>
/// <param name="ReleaseRef">Normalized channel or release reference.</param>
public sealed record RepositoryAddress(string Backend, Uri BaseUri, string ProductId, string ReleaseRef)
{
    private const char CoordinateSeparator = '@';
    private const char PathSeparator = '/';
    private const char WindowsPathSeparator = '\\';
    private const char WindowsDriveSeparator = ':';
    private const int DriveLetterIndex = 0;
    private const int DriveSeparatorIndex = 1;
    private const int DriveRootSeparatorIndex = 2;
    private const int MinimumWindowsAbsolutePathLength = 3;
    private const string ReleasePrefix = "release:";
    private const string ChannelPrefix = "channel:";
    private const string HttpScheme = "http";
    private const string HttpsScheme = "https";
    private const string S3Scheme = "s3";
    private const string FtpScheme = "ftp";
    private const string FileScheme = "file";
    private const string LocalBackend = "local";
    private const string FileRootUri = "file:///";
    private const string FileAuthorityPrefix = "file://";

    private static readonly string[] SupportedSchemes =
        [HttpScheme, HttpsScheme, S3Scheme, FtpScheme, FileScheme];

    /// <summary>Parses a repository address and normalizes its release coordinate.</summary>
    /// <param name="value">Address ending in an <c>@channel</c> or explicit release reference.</param>
    /// <returns>The parsed repository address.</returns>
    /// <exception cref="FormatException">The address or product identifier is invalid.</exception>
    public static RepositoryAddress Parse(string value)
    {
        var coordinateSeparator = value.LastIndexOf(CoordinateSeparator);
        if (coordinateSeparator <= 0 || coordinateSeparator == value.Length - 1)
            throw new FormatException("address must end in @channel or @release:id.");

        var coordinate = value[(coordinateSeparator + 1)..];
        var release = coordinate.StartsWith(ReleasePrefix, StringComparison.Ordinal)
            || coordinate.StartsWith(ChannelPrefix, StringComparison.Ordinal)
                ? coordinate
                : ChannelPrefix + coordinate;
        var repository = value[..coordinateSeparator];

        string backend;
        Uri baseUri;
        string product;
        if (Uri.TryCreate(repository, UriKind.Absolute, out var absolute))
        {
            if (!SupportedSchemes.Contains(absolute.Scheme, StringComparer.Ordinal))
                throw new FormatException($"Unsupported repository backend '{absolute.Scheme}'.");

            backend = absolute.Scheme;
            var path = absolute.AbsolutePath.Trim(PathSeparator);
            product = path.Split(PathSeparator).LastOrDefault()
                ?? throw new FormatException("product id is missing.");
            var productOffset = absolute.AbsolutePath.LastIndexOf(product, StringComparison.Ordinal);
            baseUri = new UriBuilder(absolute) { Path = absolute.AbsolutePath[..productOffset] }.Uri;
        }
        else
        {
            var normalized = repository.Replace(WindowsPathSeparator, PathSeparator);
            var finalSeparator = normalized.LastIndexOf(PathSeparator);
            product = finalSeparator > 0 ? normalized[(finalSeparator + 1)..] : normalized;
            if (IsWindowsAbsolutePath(normalized))
            {
                backend = FileScheme;
                var directory = normalized[..finalSeparator].TrimEnd(PathSeparator);
                baseUri = new Uri(FileRootUri + directory, UriKind.Absolute);
            }
            else if (normalized.StartsWith(PathSeparator))
            {
                backend = FileScheme;
                baseUri = new Uri(FileAuthorityPrefix + normalized[..finalSeparator].TrimEnd(PathSeparator), UriKind.Absolute);
            }
            else
            {
                backend = finalSeparator > 0 ? normalized[..finalSeparator] : LocalBackend;
                baseUri = new Uri(FileRootUri);
            }
        }

        if (!Identifier.IsValid(product))
            throw new FormatException("product id is invalid.");
        return new RepositoryAddress(backend, baseUri, product, release);
    }

    private static bool IsWindowsAbsolutePath(string value) =>
        value.Length >= MinimumWindowsAbsolutePathLength
        && char.IsLetter(value[DriveLetterIndex])
        && value[DriveSeparatorIndex] == WindowsDriveSeparator
        && value[DriveRootSeparatorIndex] == PathSeparator;
}
