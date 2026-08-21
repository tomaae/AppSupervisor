using AppSupervisor.ConfigurationUI;

namespace AppSupervisor.Tests;

/// <summary>Verifies that the running-application picker excludes active Windows service processes.</summary>
public sealed class RunningProcessPickerServiceFilterTests
{
    /// <summary>Confirms an SCM-reported service process is removed while an ordinary process remains.</summary>
    [Fact]
    public void RemoveServiceProcesses_ServiceProcessId_RemovesServiceRow()
    {
        RunningProcessPickerDialog.ProcessRow[] rows =
        [
            new("Service.exe", @"C:\Apps\Service.exe", false, 101),
            new("Application.exe", @"C:\Apps\Application.exe", false, 202)
        ];

        IReadOnlyList<RunningProcessPickerDialog.ProcessRow> result =
            RunningProcessPickerDialog.RemoveServiceProcesses(rows, new HashSet<int> { 101 });

        RunningProcessPickerDialog.ProcessRow application = Assert.Single(result);
        Assert.Equal(202, application.ProcessId);
    }

    /// <summary>Confirms a normal instance survives when a service uses the same executable path.</summary>
    [Fact]
    public void RemoveServiceProcesses_SharedExecutablePath_KeepsNonServiceInstance()
    {
        const string executablePath = @"C:\Apps\DualMode.exe";
        RunningProcessPickerDialog.ProcessRow[] rows =
        [
            new("DualMode.exe", executablePath, false, 101),
            new("DualMode.exe", executablePath, false, 202)
        ];

        IReadOnlyList<RunningProcessPickerDialog.ProcessRow> filtered =
            RunningProcessPickerDialog.RemoveServiceProcesses(rows, new HashSet<int> { 101 });
        IReadOnlyList<RunningProcessPickerDialog.ProcessRow> result =
            RunningProcessPickerDialog.RemoveDuplicateProcesses(filtered);

        RunningProcessPickerDialog.ProcessRow application = Assert.Single(result);
        Assert.Equal(202, application.ProcessId);
    }
}
