namespace AppSupervisor.Health;

/// <summary>
/// Keeps a health check active whenever its owning helper and supervisor profile are active.
/// </summary>
public sealed class AlwaysActiveCondition : IHealthCheckActivationCondition
{
    /// <summary>Always permits the health check to run.</summary>
    /// <returns><see langword="true"/>.</returns>
    public bool IsActive() => true;
}
