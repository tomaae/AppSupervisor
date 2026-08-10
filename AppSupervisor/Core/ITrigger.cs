namespace AppSupervisor.Core;

/// <summary>
/// Determines whether the condition that activates a supervisor profile is currently satisfied.
/// </summary>
public interface ITrigger
{
    /// <summary>
    /// Checks whether the trigger is currently active.
    /// </summary>
    /// <returns><see langword="true"/> when the supervisor profile should be active.</returns>
    bool IsActive();
}
