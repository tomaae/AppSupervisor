namespace AppSupervisor.ServiceControl;

/// <summary>
/// Represents the runtime states reported by the Windows Service Control Manager.
/// </summary>
internal enum ServiceRuntimeState
{
    Stopped = 1,
    StartPending = 2,
    StopPending = 3,
    Running = 4,
    ContinuePending = 5,
    PausePending = 6,
    Paused = 7
}
