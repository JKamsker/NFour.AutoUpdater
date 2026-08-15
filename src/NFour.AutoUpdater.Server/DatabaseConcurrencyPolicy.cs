using Npgsql;

namespace NFour.AutoUpdater.Server;

internal static class DatabaseConcurrencyPolicy
{
    internal const int MaximumRetries = 5;
    private const int RetryBaseDelayMilliseconds = 20;

    internal static bool IsTransient(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException
                {
                    SqlState: PostgresErrorCodes.SerializationFailure
                        or PostgresErrorCodes.DeadlockDetected
                        or PostgresErrorCodes.UniqueViolation
                })
                return true;
        }
        return false;
    }

    internal static Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken) =>
        Task.Delay(
            TimeSpan.FromMilliseconds(RetryBaseDelayMilliseconds * (attempt + 1)),
            cancellationToken);
}
