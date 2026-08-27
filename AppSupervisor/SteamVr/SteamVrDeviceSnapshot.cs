namespace AppSupervisor.SteamVr;

/// <summary>Describes one supported SteamVR device observed during a scan.</summary>
internal sealed record SteamVrDeviceSnapshot(
    string SerialNumber,
    string ModelNumber,
    SteamVrDeviceClass DeviceClass,
    bool Connected,
    SteamVrDeviceRole Role = SteamVrDeviceRole.None);

/// <summary>Describes one complete, non-mutating view of SteamVR and its supported devices.</summary>
internal sealed record SteamVrSnapshot(
    bool SteamVrActive,
    DateTime? SteamVrStartedUtc,
    IReadOnlyList<SteamVrDeviceSnapshot> Devices,
    string? Error = null);

/// <summary>Provides serialized snapshots of a running SteamVR session.</summary>
internal interface ISteamVrDeviceSource : IDisposable
{
    SteamVrSnapshot Capture();
}

/// <summary>Builds consistent human-readable SteamVR device and assignment labels.</summary>
internal static class SteamVrDeviceDisplay
{
    public static string ClassName(SteamVrDeviceClass deviceClass) => deviceClass switch
    {
        SteamVrDeviceClass.Controller => "Controller",
        SteamVrDeviceClass.GenericTracker => "Tracker",
        SteamVrDeviceClass.TrackingReference => "Base station",
        _ => "Device"
    };

    public static string RoleName(SteamVrDeviceRole role) => role switch
    {
        SteamVrDeviceRole.None => "Unassigned",
        SteamVrDeviceRole.LeftHand => "Left hand",
        SteamVrDeviceRole.RightHand => "Right hand",
        SteamVrDeviceRole.OptOut => "No hand",
        SteamVrDeviceRole.Treadmill => "Treadmill",
        SteamVrDeviceRole.Stylus => "Stylus",
        SteamVrDeviceRole.Handed => "Handed",
        SteamVrDeviceRole.LeftFoot => "Left foot",
        SteamVrDeviceRole.RightFoot => "Right foot",
        SteamVrDeviceRole.LeftShoulder => "Left shoulder",
        SteamVrDeviceRole.RightShoulder => "Right shoulder",
        SteamVrDeviceRole.LeftElbow => "Left elbow",
        SteamVrDeviceRole.RightElbow => "Right elbow",
        SteamVrDeviceRole.LeftKnee => "Left knee",
        SteamVrDeviceRole.RightKnee => "Right knee",
        SteamVrDeviceRole.LeftWrist => "Left wrist",
        SteamVrDeviceRole.RightWrist => "Right wrist",
        SteamVrDeviceRole.LeftAnkle => "Left ankle",
        SteamVrDeviceRole.RightAnkle => "Right ankle",
        SteamVrDeviceRole.Waist => "Waist",
        SteamVrDeviceRole.Chest => "Chest",
        SteamVrDeviceRole.Camera => "Camera",
        SteamVrDeviceRole.Keyboard => "Keyboard",
        _ => "Unknown"
    };

    public static string Description(
        string name,
        SteamVrDeviceClass deviceClass,
        SteamVrDeviceRole role)
    {
        string kind = ClassName(deviceClass).ToLowerInvariant();

        if (deviceClass == SteamVrDeviceClass.TrackingReference)
            return $"{name} — {ClassName(deviceClass)}";

        return $"{name} — {RoleName(role)} {kind}";
    }
}
