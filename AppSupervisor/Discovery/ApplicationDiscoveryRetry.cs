namespace AppSupervisor.Discovery;

/// <summary>Retries short installed-application catalog operations before reporting a provider failure.</summary>
internal static class ApplicationDiscoveryRetry
{
    private const int DefaultAttemptCount = 4;
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(3);

    /// <summary>Runs one discovery operation and retries it up to three times after failures.</summary>
    /// <typeparam name="T">The discovered catalog type.</typeparam>
    /// <param name="providerName">The user-readable catalog provider.</param>
    /// <param name="operation">The complete discovery operation.</param>
    /// <returns>The first successful catalog result.</returns>
    public static T Execute<T>(string providerName, Func<T> operation) =>
        Execute(providerName, operation, DefaultAttemptCount, Thread.Sleep);

    /// <summary>Runs a discovery operation using injectable retry timing for deterministic tests.</summary>
    /// <typeparam name="T">The discovered catalog type.</typeparam>
    /// <param name="providerName">The user-readable catalog provider.</param>
    /// <param name="operation">The complete discovery operation.</param>
    /// <param name="attemptCount">The maximum number of attempts.</param>
    /// <param name="wait">The delay implementation used between failures.</param>
    /// <returns>The first successful catalog result.</returns>
    internal static T Execute<T>(
        string providerName,
        Func<T> operation,
        int attemptCount,
        Action<TimeSpan> wait)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(wait);

        if (attemptCount < 1)
            throw new ArgumentOutOfRangeException(nameof(attemptCount));

        Exception? finalException = null;

        for (int attempt = 1; attempt <= attemptCount; attempt++)
        {
            try
            {
                return operation();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                finalException = exception;

                if (attempt < attemptCount)
                    wait(DefaultRetryDelay);
            }
        }

        throw new ApplicationDiscoveryException(
            providerName,
            attemptCount,
            finalException ?? new InvalidOperationException("Application discovery failed.")
        );
    }
}
