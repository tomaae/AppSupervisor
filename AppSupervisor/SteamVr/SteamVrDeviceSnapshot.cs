namespace AppSupervisor.SteamVr;

/// <summary>Describes one SteamVR tracker or tracking reference observed during a scan.</summary>
internal sealed record SteamVrDeviceSnapshot(
    string SerialNumber,
    string ModelNumber,
    SteamVrDeviceClass DeviceClass,
    bool Connected);

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

