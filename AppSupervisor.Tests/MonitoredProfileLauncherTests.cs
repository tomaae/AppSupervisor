using System.Diagnostics;
using AppSupervisor.StreamDeck;

namespace AppSupervisor.Tests;

/// <summary>Verifies guarded launches requested by the companion Stream Deck plugin.</summary>
public sealed class MonitoredProfileLauncherTests
{
    [Fact]
    public void Launch_FullExecutablePath_StartsConfiguredTarget()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"AppSupervisor.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string executablePath = Path.Combine(directory, "monitored.exe");
        File.WriteAllBytes(executablePath, []);
        ProcessStartInfo? captured = null;

        try
        {
            MonitoredProfileLaunchOutcome outcome = MonitoredProfileLauncher.Launch(
                CreateProfile(executablePath),
                _ => false,
                startInfo =>
                {
                    captured = startInfo;
                    return null;
                }
            );

            Assert.Equal(MonitoredProfileLaunchOutcome.Started, outcome);
            Assert.NotNull(captured);
            Assert.Equal(executablePath, captured.FileName);
            Assert.Equal(directory, captured.WorkingDirectory);
            Assert.True(captured.UseShellExecute);
        }
        finally
        {
            File.Delete(executablePath);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void Launch_AlreadyRunning_DoesNotStartDuplicate()
    {
        bool startCalled = false;

        MonitoredProfileLaunchOutcome outcome = MonitoredProfileLauncher.Launch(
            CreateProfile("monitored.exe"),
            _ => true,
            _ =>
            {
                startCalled = true;
                return null;
            }
        );

        Assert.Equal(MonitoredProfileLaunchOutcome.AlreadyRunning, outcome);
        Assert.False(startCalled);
    }

    [Fact]
    public void Launch_DisabledOrNonProcessProfile_IsRejected()
    {
        SupervisorProfileConfig disabled = CreateProfile("monitored.exe");
        disabled.Enabled = false;
        SupervisorProfileConfig bluetooth = CreateProfile("monitored.exe");
        bluetooth.TriggerType = ProfileTriggerType.BluetoothDevice;

        Assert.Throws<InvalidOperationException>(() => Launch(disabled));
        Assert.Throws<InvalidOperationException>(() => Launch(bluetooth));
    }

    [Fact]
    public void Launch_MissingFullPath_IsRejected()
    {
        string missingPath = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.Tests.{Guid.NewGuid():N}",
            "missing.exe"
        );

        Assert.Throws<FileNotFoundException>(() => Launch(CreateProfile(missingPath)));
    }

    private static SupervisorProfileConfig CreateProfile(string monitorProcess) => new()
    {
        Name = "Test profile",
        MonitorProcess = monitorProcess
    };

    private static MonitoredProfileLaunchOutcome Launch(SupervisorProfileConfig profile) =>
        MonitoredProfileLauncher.Launch(profile, _ => false, _ => null);
}
