using Microsoft.EntityFrameworkCore;

namespace NFour.AutoUpdater.Server;

/// <summary>Bounds telemetry storage independently of the append-only ingest path.</summary>
public sealed class TelemetryRetentionService(IDbContextFactory<ManagementDbContext> databaseFactory, ILogger<TelemetryRetentionService> logger) : BackgroundService
{
    private const int RetentionIntervalHours = 24;
    private const int DefaultRetentionDays = 30;
    private const int MinimumRetentionDays = 1;
    private const int MaximumRetentionDays = 3650;
    private const string RetentionDaysEnvironmentVariable = "FOURSUP_TELEMETRY_RETENTION_DAYS";
    private const string RetentionFailureLogMessage = "Telemetry retention pass failed.";

    private static readonly TimeSpan Interval = TimeSpan.FromHours(RetentionIntervalHours);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DeleteExpiredAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, RetentionFailureLogMessage); }
            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        var days = DefaultRetentionDays;
        if (int.TryParse(Environment.GetEnvironmentVariable(RetentionDaysEnvironmentVariable), out var configured))
            days = Math.Clamp(configured, MinimumRetentionDays, MaximumRetentionDays);
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var cutoff = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromDays(days));
        await database.Telemetry.Where(row => row.At < cutoff).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
