using AppSupervisor.ConfigurationUI;

namespace AppSupervisor.Tests;

/// <summary>Verifies the running-process picker's default Microsoft and Windows filtering rules.</summary>
public sealed class RunningProcessPickerFilterTests
{
    private const string WindowsDirectory = @"C:\Windows";

    /// <summary>Confirms executables installed beneath the Windows directory are hidden.</summary>
    [Fact]
    public void IsStandardMicrosoftProcess_WindowsPath_ReturnsTrue()
    {
        bool result = RunningProcessPickerDialog.IsStandardMicrosoftProcess(
            "svchost.exe",
            @"C:\Windows\System32\svchost.exe",
            companyName: null,
            WindowsDirectory
        );

        Assert.True(result);
    }

    /// <summary>Confirms Microsoft-published executables outside the Windows directory are hidden.</summary>
    [Fact]
    public void IsStandardMicrosoftProcess_MicrosoftPublisher_ReturnsTrue()
    {
        bool result = RunningProcessPickerDialog.IsStandardMicrosoftProcess(
            "msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            "Microsoft Corporation",
            WindowsDirectory
        );

        Assert.True(result);
    }

    /// <summary>Confirms known inaccessible core Windows processes are hidden by name.</summary>
    [Fact]
    public void IsStandardMicrosoftProcess_CoreProcessWithoutPath_ReturnsTrue()
    {
        bool result = RunningProcessPickerDialog.IsStandardMicrosoftProcess(
            "System.exe",
            executablePath: null,
            companyName: null,
            WindowsDirectory
        );

        Assert.True(result);
    }

    /// <summary>Confirms ordinary third-party applications remain visible.</summary>
    [Fact]
    public void IsStandardMicrosoftProcess_ThirdPartyApplication_ReturnsFalse()
    {
        bool result = RunningProcessPickerDialog.IsStandardMicrosoftProcess(
            "VRCFaceTracking.exe",
            @"D:\Apps\VRCFaceTracking.exe",
            "benaclejames",
            WindowsDirectory
        );

        Assert.False(result);
    }
}
