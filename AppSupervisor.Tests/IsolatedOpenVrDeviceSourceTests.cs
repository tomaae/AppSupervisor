using System.Text.Json;
using AppSupervisor.SteamVr;

namespace AppSupervisor.Tests;

/// <summary>Verifies the native OpenVR child-process transport without loading OpenVR in the test process.</summary>
public sealed class IsolatedOpenVrDeviceSourceTests
{
    /// <summary>Confirms valid JSON is retained even when native cleanup later gives the child a failure exit code.</summary>
    [Fact]
    public void ParseCaptureOutput_ValidSnapshotWithFailedExit_ReturnsSnapshot()
    {
        var expected = new SteamVrSnapshot(
            true,
            new DateTime(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc),
            [
                new SteamVrDeviceSnapshot(
                    "LHR-TEST",
                    "Tracker",
                    SteamVrDeviceClass.GenericTracker,
                    true,
                    SteamVrDeviceRole.LeftKnee
                )
            ]
        );
        string output = JsonSerializer.Serialize(expected);

        SteamVrSnapshot actual = IsolatedOpenVrDeviceSource.ParseCaptureOutput(
            output,
            "native cleanup failed",
            exitCode: -1073741819,
            vrServerRunning: false
        );

        Assert.True(actual.SteamVrActive);
        Assert.Null(actual.Error);
        SteamVrDeviceSnapshot device = Assert.Single(actual.Devices);
        Assert.Equal("LHR-TEST", device.SerialNumber);
        Assert.True(device.Connected);
        Assert.Equal(SteamVrDeviceRole.LeftKnee, device.Role);
    }

    /// <summary>Confirms missing or corrupt child output becomes an ordinary source error.</summary>
    [Fact]
    public void ParseCaptureOutput_InvalidSnapshot_ContainsFailure()
    {
        SteamVrSnapshot actual = IsolatedOpenVrDeviceSource.ParseCaptureOutput(
            "not-json",
            "",
            exitCode: 5,
            vrServerRunning: true
        );

        Assert.True(actual.SteamVrActive);
        Assert.Empty(actual.Devices);
        Assert.Contains("exited with code 5", actual.Error);
    }

    /// <summary>Confirms ordinary tray invocations are not mistaken for the private capture host.</summary>
    [Fact]
    public void TryRun_OrdinaryArguments_DoesNotCapture()
    {
        Assert.False(OpenVrSnapshotHost.TryRun([]));
        Assert.False(OpenVrSnapshotHost.TryRun(["--unrelated"]));
    }
}
