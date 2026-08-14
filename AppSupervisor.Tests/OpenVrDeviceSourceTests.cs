using AppSupervisor.SteamVr;

namespace AppSupervisor.Tests;

/// <summary>Verifies OpenVR controller and tracker role translation without loading the native runtime.</summary>
public sealed class OpenVrDeviceSourceTests
{
    [Theory]
    [InlineData(1, SteamVrDeviceRole.LeftHand)]
    [InlineData(2, SteamVrDeviceRole.RightHand)]
    [InlineData(5, SteamVrDeviceRole.Stylus)]
    [InlineData(0, SteamVrDeviceRole.None)]
    public void MapControllerRole_KnownNativeValues_ReturnsRole(
        int nativeRole,
        SteamVrDeviceRole expected)
    {
        Assert.Equal(expected, OpenVrDeviceSource.MapControllerRole(nativeRole));
    }

    [Theory]
    [InlineData("TrackerRole_LeftFoot", SteamVrDeviceRole.LeftFoot)]
    [InlineData("TrackerRole_RightKnee", SteamVrDeviceRole.RightKnee)]
    [InlineData("TrackerRole_Waist", SteamVrDeviceRole.Waist)]
    [InlineData("", SteamVrDeviceRole.None)]
    public void MapTrackerRole_SteamVrSetting_ReturnsRole(
        string setting,
        SteamVrDeviceRole expected)
    {
        Assert.Equal(expected, OpenVrDeviceSource.MapTrackerRole(setting));
    }

    [Fact]
    public void BuildTrackerSettingsKey_UsesOpenVrDevicePath()
    {
        Assert.Equal(
            "/devices/lighthouse/LHR-TEST",
            OpenVrDeviceSource.BuildTrackerSettingsKey("lighthouse", "LHR-TEST")
        );
    }
}
