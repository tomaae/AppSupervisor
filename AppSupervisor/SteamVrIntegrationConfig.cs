using AppSupervisor.Notifications;

namespace AppSupervisor;

/// <summary>Configures global observation-only monitoring of expected SteamVR devices.</summary>
public sealed class SteamVrIntegrationConfig
{
    /// <summary>Gets or sets whether the monitor attaches while SteamVR is running.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the minutes between reminders for an unsilenced offline device.</summary>
    public int ReminderIntervalMinutes { get; set; } = 5;

    /// <summary>Gets or sets the expected tracked devices identified by stable SteamVR serial number.</summary>
    public List<SteamVrDeviceConfig> Devices { get; set; } = [];

    /// <summary>Gets or sets supplemental notification destinations; the modeless alert window is always used.</summary>
    public NotificationConfig Notifications { get; set; } = new()
    {
        Target = [NotificationTarget.XsOverlay]
    };
}

/// <summary>Identifies one expected SteamVR tracker or tracking reference.</summary>
public sealed class SteamVrDeviceConfig
{
    /// <summary>Gets or sets whether this expected device participates in monitoring.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the immutable serial reported by OpenVR.</summary>
    public string SerialNumber { get; set; } = "";

    /// <summary>Gets or sets the user-facing alias used in alerts.</summary>
    public string Name { get; set; } = "";

    /// <summary>Gets or sets the device class captured during discovery.</summary>
    public SteamVrDeviceClass DeviceClass { get; set; }

    /// <summary>Gets or sets the model string captured during discovery.</summary>
    public string ModelNumber { get; set; } = "";
}

/// <summary>Lists the observation-worthy OpenVR device classes supported by this integration.</summary>
public enum SteamVrDeviceClass
{
    /// <summary>A generic tracked accessory such as a Vive Tracker.</summary>
    GenericTracker,

    /// <summary>A tracking reference such as a Lighthouse base station.</summary>
    TrackingReference
}
