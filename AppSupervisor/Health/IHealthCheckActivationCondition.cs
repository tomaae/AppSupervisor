namespace AppSupervisor.Health;

/// <summary>
/// Determines whether a health check is currently applicable.
/// </summary>
public interface IHealthCheckActivationCondition
{
    /// <summary>Returns whether the external prerequisite for the check is running.</summary>
    /// <returns><see langword="true"/> when the check should run.</returns>
    bool IsActive();
}
