using System.Diagnostics;
using AppSupervisor.Triggers;

namespace AppSupervisor.Tests;

/// <summary>
/// Verifies process-trigger matching against the operating system process list.
/// </summary>
public sealed class ProcessTriggerTests
{
    /// <summary>
    /// Confirms that the trigger recognizes the process hosting the current test run.
    /// </summary>
    [Fact]
    public void IsActive_CurrentProcessName_ReturnsTrue()
    {
        using Process currentProcess = Process.GetCurrentProcess();
        var trigger = new ProcessTrigger(currentProcess.ProcessName + ".exe");

        Assert.True(trigger.IsActive());
    }

    /// <summary>
    /// Confirms that a unique nonexistent process name leaves the trigger inactive.
    /// </summary>
    [Fact]
    public void IsActive_NonexistentProcessName_ReturnsFalse()
    {
        var trigger = new ProcessTrigger($"AppSupervisorMissing-{Guid.NewGuid():N}.exe");

        Assert.False(trigger.IsActive());
    }
}
