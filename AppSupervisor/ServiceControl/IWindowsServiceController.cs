namespace AppSupervisor.ServiceControl;

/// <summary>
/// Defines the Windows Service Control Manager operations used by a managed service.
/// </summary>
internal interface IWindowsServiceController : IDisposable
{
    /// <summary>
    /// Verifies all required service permissions and changes any non-Manual startup mode to Manual.
    /// </summary>
    void EnsureManualStartAndRequiredAccess();

    /// <summary>
    /// Reads the service's current runtime state.
    /// </summary>
    /// <returns>The current state reported by Windows.</returns>
    ServiceRuntimeState GetState();

    /// <summary>
    /// Requests the service to start.
    /// </summary>
    void Start();

    /// <summary>
    /// Requests the service to stop gracefully through the Service Control Manager.
    /// </summary>
    void Stop();

    /// <summary>
    /// Requests a paused service to continue.
    /// </summary>
    void Continue();
}
