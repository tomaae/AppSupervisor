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
    [InlineData("TrackerRole_LeftWrist", SteamVrDeviceRole.LeftWrist)]
    [InlineData("TrackerRole_RightAnkle", SteamVrDeviceRole.RightAnkle)]
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

    [Theory]
    [InlineData("vive_tracker_handed", SteamVrDeviceRole.Handed)]
    [InlineData("vive_tracker_left_foot", SteamVrDeviceRole.LeftFoot)]
    [InlineData("vive_tracker_right_foot", SteamVrDeviceRole.RightFoot)]
    [InlineData("vive_tracker_left_shoulder", SteamVrDeviceRole.LeftShoulder)]
    [InlineData("vive_tracker_right_shoulder", SteamVrDeviceRole.RightShoulder)]
    [InlineData("vive_tracker_left_elbow", SteamVrDeviceRole.LeftElbow)]
    [InlineData("vive_tracker_right_elbow", SteamVrDeviceRole.RightElbow)]
    [InlineData("vive_tracker_left_knee", SteamVrDeviceRole.LeftKnee)]
    [InlineData("vive_tracker_right_knee", SteamVrDeviceRole.RightKnee)]
    [InlineData("vive_tracker_left_wrist", SteamVrDeviceRole.LeftWrist)]
    [InlineData("vive_tracker_right_wrist", SteamVrDeviceRole.RightWrist)]
    [InlineData("vive_tracker_left_ankle", SteamVrDeviceRole.LeftAnkle)]
    [InlineData("vive_tracker_right_ankle", SteamVrDeviceRole.RightAnkle)]
    [InlineData("vive_tracker_waist", SteamVrDeviceRole.Waist)]
    [InlineData("vive_tracker_chest", SteamVrDeviceRole.Chest)]
    [InlineData("vive_tracker_camera", SteamVrDeviceRole.Camera)]
    [InlineData("vive_tracker_keyboard", SteamVrDeviceRole.Keyboard)]
    [InlineData("vive_tracker", SteamVrDeviceRole.None)]
    [InlineData("", SteamVrDeviceRole.None)]
    public void MapTrackerControllerType_RoleSpecificProfile_ReturnsRole(
        string controllerType,
        SteamVrDeviceRole expected)
    {
        Assert.Equal(expected, OpenVrDeviceSource.MapTrackerControllerType(controllerType));
    }
}
