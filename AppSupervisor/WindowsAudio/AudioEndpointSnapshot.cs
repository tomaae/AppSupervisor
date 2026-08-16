namespace AppSupervisor.WindowsAudio;

/// <summary>Describes one active Windows audio endpoint and its durable matching signals.</summary>
internal sealed record AudioEndpointSnapshot(
    string EndpointId,
    string DeviceInstanceId,
    string ContainerId,
    string FriendlyName,
    string InterfaceName,
    AudioInterfaceDirection Direction,
    bool FollowsDefault = false)
{
    public string DisplayName => FollowsDefault
        ? $"Default {(Direction == AudioInterfaceDirection.Output ? "output" : "input")} — {FriendlyName}"
        : $"{FriendlyName} ({(Direction == AudioInterfaceDirection.Output ? "output" : "input")})";

    public void CopyIdentityTo(AudioInterfaceResourceConfig configuration)
    {
        configuration.EndpointId = EndpointId;
        configuration.DeviceInstanceId = DeviceInstanceId;
        configuration.ContainerId = ContainerId;
        configuration.FriendlyName = FriendlyName;
        configuration.InterfaceName = InterfaceName;
        configuration.Direction = Direction;
        configuration.UseDefaultDevice = FollowsDefault;
    }
}

/// <summary>Contains the mutable master state of one Windows audio endpoint.</summary>
internal readonly record struct AudioEndpointState(float VolumeScalar, bool Muted);
