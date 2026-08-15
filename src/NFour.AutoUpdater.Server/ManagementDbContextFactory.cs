using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NFour.AutoUpdater.Server;

/// <summary>Creates the management database context for Entity Framework design-time tools.</summary>
public sealed class ManagementDbContextFactory : IDesignTimeDbContextFactory<ManagementDbContext>
{
    private const string DatabaseEnvironmentVariable = "FOURSUP_DATABASE";
    private const string DevelopmentConnection = "Host=localhost;Database=4sup;Username=4sup";

    /// <summary>Creates a context from the configured database connection.</summary>
    /// <param name="args">Arguments supplied by Entity Framework tooling.</param>
    /// <returns>A design-time management database context.</returns>
    public ManagementDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable(DatabaseEnvironmentVariable)
            ?? DevelopmentConnection;
        var options = new DbContextOptionsBuilder<ManagementDbContext>().UseNpgsql(connection).Options;
        return new ManagementDbContext(options);
    }
}
