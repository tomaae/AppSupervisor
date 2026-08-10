using AppSupervisor.Core;

namespace AppSupervisor.Resources;

/// <summary>
/// Exposes the process lifecycle state required by the health-check recovery wrapper.
/// </summary>
internal interface IManagedApplicationLifecycle : IManagedResource
{
    /// <summary>Gets the helper configuration used for identity and process discovery.</summary>
    ManagedApplicationConfig Config { get; }

    /// <summary>Gets whether a graceful or force-enabled close operation still requires supervision.</summary>
    bool CloseOperationPending { get; }

    /// <summary>Checks whether at least one matching helper process is currently running.</summary>
    /// <returns><see langword="true"/> when a matching process exists.</returns>
    bool IsRunning();
}
