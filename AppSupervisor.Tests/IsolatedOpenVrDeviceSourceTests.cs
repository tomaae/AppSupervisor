using System.Text.Json;
using AppSupervisor.SteamVr;

namespace AppSupervisor.Tests;

/// <summary>Verifies the native OpenVR child-process transport without loading OpenVR in the test process.</summary>
public sealed class IsolatedOpenVrDeviceSourceTests
{
    [Theory]
    [InlineData(15, SteamVrDeviceRole.Waist)]
    [InlineData(16, SteamVrDeviceRole.Chest)]
    [InlineData(17, SteamVrDeviceRole.Camera)]
    [InlineData(18, SteamVrDeviceRole.Keyboard)]
    [InlineData(19, SteamVrDeviceRole.LeftWrist)]
    [InlineData(20, SteamVrDeviceRole.RightWrist)]
    [InlineData(21, SteamVrDeviceRole.LeftAnkle)]
    [InlineData(22, SteamVrDeviceRole.RightAnkle)]
    public void ParseCaptureOutput_StableRoleWireValue_ReturnsExpectedRole(
        int wireValue,
        SteamVrDeviceRole expectedRole)
    {
        string output = $$"""
            {"SteamVrActive":true,"SteamVrStartedUtc":null,"Devices":[{"SerialNumber":"LHR-TEST","ModelNumber":"Tracker","DeviceClass":1,"Connected":true,"Role":{{wireValue}}}],"Error":null}
            """;

        SteamVrSnapshot snapshot = IsolatedOpenVrDeviceSource.ParseCaptureOutput(
            output,
            "",
            exitCode: 0,
            vrServerRunning: true
        );

        SteamVrDeviceSnapshot device = Assert.Single(snapshot.Devices);
        Assert.Equal(expectedRole, device.Role);
    }

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
