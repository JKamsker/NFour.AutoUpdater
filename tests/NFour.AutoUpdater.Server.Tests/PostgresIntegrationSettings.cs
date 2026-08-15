namespace NFour.AutoUpdater.Server.Tests;

internal static class PostgresIntegrationSettings
{
    public const string GateEnvironmentVariable = "FOURSUP_POSTGRES";
    public const string DatabaseEnvironmentVariable = "FOURSUP_DATABASE";
    public const string EnabledEnvironmentValue = "1";

    public static string GetRequiredConnectionString()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(GateEnvironmentVariable), EnabledEnvironmentValue, StringComparison.Ordinal))
            Assert.Skip($"Set {GateEnvironmentVariable}={EnabledEnvironmentValue} and {DatabaseEnvironmentVariable} to run PostgreSQL integration tests.");
        return Environment.GetEnvironmentVariable(DatabaseEnvironmentVariable)
            ?? throw new InvalidOperationException($"The {DatabaseEnvironmentVariable} environment variable is required for PostgreSQL integration tests.");
    }
}
