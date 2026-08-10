using System.Diagnostics;
using AppSupervisor.Core;

namespace AppSupervisor.Triggers;

/// <summary>
/// Activates a supervisor profile while a process with the configured executable name is running.
/// </summary>
public sealed class ProcessTrigger : ITrigger
{
    private readonly string _processName;

    /// <summary>
    /// Creates a trigger that detects a process by executable name.
    /// </summary>
    /// <param name="processPathOrName">An executable path or name whose extension is ignored for lookup.</param>
    public ProcessTrigger(string processPathOrName)
    {
        _processName = Path.GetFileNameWithoutExtension(processPathOrName);
    }

    /// <summary>
    /// Checks whether at least one process with the configured name is currently running.
    /// </summary>
    /// <returns><see langword="true"/> when a matching process exists.</returns>
    public bool IsActive()
    {
        Process[] processes = Process.GetProcessesByName(_processName);

        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }
    }
}
