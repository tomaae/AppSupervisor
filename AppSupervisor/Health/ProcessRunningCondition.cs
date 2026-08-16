using AppSupervisor.Core;

namespace AppSupervisor.Health;

/// <summary>
/// Activates a health check only while at least one instance of a named process exists.
/// </summary>
public sealed class ProcessRunningCondition : IHealthCheckActivationCondition
{
    private readonly string _processName;

    /// <summary>Creates a process-presence activation condition.</summary>
    /// <param name="processName">A process name with or without its executable extension.</param>
    public ProcessRunningCondition(string processName)
    {
        _processName = Path.GetFileNameWithoutExtension(processName);
    }

    /// <summary>Checks process presence and disposes every temporary process wrapper.</summary>
    /// <returns><see langword="true"/> when one or more matching processes exist.</returns>
    public bool IsActive()
    {
        return ProcessPathSnapshot.IsProcessNameRunning(_processName);
    }
}
