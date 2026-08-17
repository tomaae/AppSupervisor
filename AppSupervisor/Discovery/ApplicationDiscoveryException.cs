namespace AppSupervisor.Discovery;

/// <summary>Reports that an installed-application catalog could not be read after retrying.</summary>
internal sealed class ApplicationDiscoveryException : Exception
{
    /// <summary>Creates a provider-specific discovery failure while retaining the final cause.</summary>
    /// <param name="providerName">The user-readable catalog provider.</param>
    /// <param name="attemptCount">The number of completed discovery attempts.</param>
    /// <param name="innerException">The final provider failure.</param>
    public ApplicationDiscoveryException(
        string providerName,
        int attemptCount,
        Exception innerException)
        : base(
            $"{providerName} application discovery failed after {attemptCount} " +
            $"{(attemptCount == 1 ? "attempt" : "attempts")}: {innerException.Message}",
            innerException
        )
    {
        ProviderName = providerName;
        AttemptCount = attemptCount;
    }

    /// <summary>Gets the user-readable catalog provider.</summary>
    public string ProviderName { get; }

    /// <summary>Gets the number of completed discovery attempts.</summary>
    public int AttemptCount { get; }
}
