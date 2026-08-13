namespace AppSupervisor.Core;

/// <summary>
/// Reports whether a managed resource still has asynchronous deactivation work in progress.
/// </summary>
internal interface IManagedResourceDeactivationState
{
    /// <summary>Gets whether the resource still requires deactivation supervision.</summary>
    bool DeactivationPending { get; }
}
