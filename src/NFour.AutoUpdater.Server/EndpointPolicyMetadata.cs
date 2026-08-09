namespace NFour.AutoUpdater.Server;

/// <summary>Associates an endpoint with the authorization policy enforced by middleware.</summary>
/// <param name="Policy">Authorization policy name.</param>
public sealed record EndpointPolicyMetadata(string Policy);
