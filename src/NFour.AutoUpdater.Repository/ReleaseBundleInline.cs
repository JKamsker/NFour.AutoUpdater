namespace NFour.AutoUpdater.Repository;

/// <summary>Recovers the exact pinned manifest bytes carried by one release-bundle inline entry.</summary>
internal static class ReleaseBundleInline
{
    /// <summary>
    /// Current writers store base64url of the exact bytes the release pinned; those are returned
    /// as-is. Bundles written before that change embedded the manifest as a JSON object, which is
    /// re-serialized canonically. That reproduces the pinned bytes only for manifests the writer
    /// itself serialized, and the caller's pinned-digest check rejects anything else, so accepting
    /// the legacy encoding never weakens verification.
    /// </summary>
    /// <param name="packageId">The inline entry's package id, for diagnostics.</param>
    /// <param name="value">The inline entry's JSON value.</param>
    /// <returns>The candidate manifest bytes, still to be checked against the release pin.</returns>
    public static byte[] Decode(string packageId, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                try { return Base64Url.Decode(value.GetString()!); }
                catch (FormatException) { throw new InvalidDataException($"Bundle inline manifest '{packageId}' is not valid base64url."); }
            case JsonValueKind.Object:
                return JsonSerializer.SerializeToUtf8Bytes(value, RepositoryJson.Options);
            default:
                throw new InvalidDataException($"Bundle inline manifest '{packageId}' must be a base64url string.");
        }
    }
}
