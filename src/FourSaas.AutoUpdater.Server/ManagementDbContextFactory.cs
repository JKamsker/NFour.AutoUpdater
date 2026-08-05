using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FourSaas.AutoUpdater.Server;

public sealed class ManagementDbContextFactory : IDesignTimeDbContextFactory<ManagementDbContext>
{
    public ManagementDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("FOURSUP_DATABASE")
            ?? "Host=localhost;Database=4sup;Username=4sup;Password=4sup";
        var options = new DbContextOptionsBuilder<ManagementDbContext>().UseNpgsql(connection).Options;
        return new ManagementDbContext(options);
    }
}
