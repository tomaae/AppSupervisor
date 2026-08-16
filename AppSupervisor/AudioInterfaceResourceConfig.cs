namespace AppSupervisor;

/// <summary>Configures volume and mute state for one Windows audio endpoint.</summary>
public sealed class AudioInterfaceResourceConfig : ManagedResourceConfig
{
    /// <summary>Gets or sets the most recently observed Windows endpoint identifier.</summary>
    public string EndpointId { get; set; } = "";

    /// <summary>Gets or sets the PnP device-instance identity used when the endpoint ID changes.</summary>
    public string DeviceInstanceId { get; set; } = "";

    /// <summary>Gets or sets the physical device container identity used across endpoint re-enumeration.</summary>
    public string ContainerId { get; set; } = "";

    /// <summary>Gets or sets the Windows endpoint friendly name captured during selection.</summary>
    public string FriendlyName { get; set; } = "";

    /// <summary>Gets or sets the device-interface friendly name captured during selection.</summary>
    public string InterfaceName { get; set; } = "";

    /// <summary>Gets or sets whether this is an output or input endpoint.</summary>
    public AudioInterfaceDirection Direction { get; set; }

    /// <summary>Gets or sets whether this action follows the current Windows default for its direction.</summary>
    public bool UseDefaultDevice { get; set; }

    /// <summary>Gets or sets the requested master volume percentage.</summary>
    public int VolumePercent { get; set; } = 100;

    /// <summary>Gets or sets the requested endpoint mute state.</summary>
    public bool Muted { get; set; }

    /// <summary>Gets or sets whether the pre-activation state is restored when the profile closes.</summary>
    public bool RestoreOnDeactivate { get; set; } = true;

    /// <summary>Gets or sets audio endpoint-specific notification targets.</summary>
    public NotificationConfig Notifications { get; set; } = new();
}
