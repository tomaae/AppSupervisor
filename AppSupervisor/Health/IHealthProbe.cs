namespace AppSupervisor.Health;

/// <summary>
/// Evaluates one asynchronous health signal for the currently running helper process set.
/// </summary>
public interface IHealthProbe : IDisposable
{
    /// <summary>Runs one cancellable health probe.</summary>
    /// <param name="ownerProcessIds">The process identifiers currently matching the managed helper executable.</param>
    /// <param name="cancellationToken">Cancels the probe on pause, reload, shutdown, or deactivation.</param>
    /// <returns>The probe outcome and diagnostic detail.</returns>
    Task<HealthProbeResult> CheckAsync(
        IReadOnlySet<int> ownerProcessIds,
        CancellationToken cancellationToken);

    /// <summary>Clears state retained between probe samples, such as OSC parameter freshness history.</summary>
    void Reset();
}
