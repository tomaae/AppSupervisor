using AppSupervisor.ConfigurationUI;

namespace AppSupervisor.Tests;

/// <summary>Verifies unique executable presentation in the running-application picker.</summary>
public sealed class RunningProcessPickerDeduplicationTests
{
    /// <summary>Confirms multiple instances of one executable path collapse into one row.</summary>
    [Fact]
    public void RemoveDuplicateProcesses_SameExecutablePath_ReturnsOneRow()
    {
        RunningProcessPickerDialog.ProcessRow[] rows =
        [
            new("Helper.exe", @"C:\Apps\Helper.exe", false),
            new("Helper.exe", @"c:\apps\helper.exe", false)
        ];

        IReadOnlyList<RunningProcessPickerDialog.ProcessRow> result =
            RunningProcessPickerDialog.RemoveDuplicateProcesses(rows);

        Assert.Single(result);
    }

    /// <summary>Confirms identical filenames from distinct installation paths remain selectable.</summary>
    [Fact]
    public void RemoveDuplicateProcesses_DifferentExecutablePaths_KeepsBothRows()
    {
        RunningProcessPickerDialog.ProcessRow[] rows =
        [
            new("Helper.exe", @"C:\Apps\One\Helper.exe", false),
            new("Helper.exe", @"D:\Apps\Two\Helper.exe", false)
        ];

        IReadOnlyList<RunningProcessPickerDialog.ProcessRow> result =
            RunningProcessPickerDialog.RemoveDuplicateProcesses(rows);

        Assert.Equal(2, result.Count);
    }

    /// <summary>Confirms inaccessible repeated process names collapse when no path is available.</summary>
    [Fact]
    public void RemoveDuplicateProcesses_UnavailablePaths_UsesProcessName()
    {
        RunningProcessPickerDialog.ProcessRow[] rows =
        [
            new("Restricted.exe", null, false),
            new("restricted.exe", null, false)
        ];

        IReadOnlyList<RunningProcessPickerDialog.ProcessRow> result =
            RunningProcessPickerDialog.RemoveDuplicateProcesses(rows);

        Assert.Single(result);
    }
}
