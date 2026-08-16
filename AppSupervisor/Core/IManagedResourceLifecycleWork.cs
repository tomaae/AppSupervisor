namespace AppSupervisor.Core;

/// <summary>Exposes short-interval lifecycle work that must settle independently of the one-second supervisor pass.</summary>
internal interface IManagedResourceLifecycleWork
{
    /// <summary>Gets whether the resource still owns or awaits a lifecycle transition.</summary>
    bool LifecycleWorkPending { get; }

    /// <summary>Advances pending start, close, or post-launch work without blocking.</summary>
    /// <param name="nowUtc">The current lifecycle-cycle timestamp.</param>
    /// <returns>Whether this pass confirmed an ordinary resource restart.</returns>
    ManagedResourceUpdate AdvanceLifecycle(DateTime nowUtc);
}
