using Microsoft.EntityFrameworkCore;

namespace NFour.AutoUpdater.Server;

/// <summary>Bounds telemetry storage independently of the append-only ingest path.</summary>
public sealed class TelemetryRetentionService(IDbContextFactory<ManagementDbContext> databaseFactory, ILogger<TelemetryRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DeleteExpiredAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Telemetry retention pass failed."); }
            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        var days = 30;
        if (int.TryParse(Environment.GetEnvironmentVariable("FOURSUP_TELEMETRY_RETENTION_DAYS"), out var configured))
            days = Math.Clamp(configured, 1, 3650);
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var cutoff = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromDays(days));
        await database.Telemetry.Where(row => row.At < cutoff).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
