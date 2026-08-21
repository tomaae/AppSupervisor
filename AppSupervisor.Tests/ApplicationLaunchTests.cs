using AppSupervisor.Configuration;

namespace AppSupervisor.Tests;

/// <summary>Verifies deterministic launch requests for direct executables and shell URIs.</summary>
public sealed class ApplicationLaunchTests
{
    [Fact]
    public void CreateStartInfo_DirectExecutable_UsesExecutableDirectory()
    {
        string executablePath = Path.Combine(
            Path.GetTempPath(),
            "Helper directory",
            "helper.exe"
        );
        var configuration = new ManagedApplicationConfig
        {
            Path = executablePath,
            Arguments = "--test"
        };

        System.Diagnostics.ProcessStartInfo startInfo =
            ApplicationUri.CreateStartInfo(configuration);

        Assert.Equal(executablePath, startInfo.FileName);
        Assert.Equal("--test", startInfo.Arguments);
        Assert.Equal(Path.GetDirectoryName(executablePath), startInfo.WorkingDirectory);
    }

    [Fact]
    public void CreateStartInfo_SteamUri_UsesMonitoredExecutableDirectory()
    {
        string executablePath = Path.Combine(
            Path.GetTempPath(),
            "Steam library",
            "game.exe"
        );
        var configuration = new ManagedApplicationConfig
        {
            Path = executablePath,
            AppUri = "steam://rungameid/12345"
        };

        System.Diagnostics.ProcessStartInfo startInfo =
            ApplicationUri.CreateStartInfo(configuration);

        Assert.Equal(configuration.AppUri, startInfo.FileName);
        Assert.Equal(Path.GetDirectoryName(executablePath), startInfo.WorkingDirectory);
    }
}
