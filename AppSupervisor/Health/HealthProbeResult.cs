namespace AppSupervisor.Health;

/// <summary>
/// Contains the outcome and user-readable detail from one health probe attempt.
/// </summary>
public readonly record struct HealthProbeResult(bool Healthy, string Detail)
{
    /// <summary>Creates a successful probe result.</summary>
    /// <param name="detail">The optional success detail.</param>
    /// <returns>A healthy probe result.</returns>
    public static HealthProbeResult Success(string detail = "") => new(true, detail);

    /// <summary>Creates a failed probe result.</summary>
    /// <param name="detail">The reason the probe is unhealthy.</param>
    /// <returns>An unhealthy probe result.</returns>
    public static HealthProbeResult Failure(string detail) => new(false, detail);
}
