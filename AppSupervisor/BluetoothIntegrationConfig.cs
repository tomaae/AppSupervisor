namespace AppSupervisor;

/// <summary>Configures application-wide Bluetooth registration and presence timing.</summary>
public sealed class BluetoothIntegrationConfig
{
    /// <summary>Gets or sets the interval between nearby-device discovery cycles.</summary>
    public int ScanIntervalSeconds { get; set; } = 15;

    /// <summary>Gets or sets how long a registered device may remain unseen before becoming absent.</summary>
    public int PresenceTimeoutSeconds { get; set; } = 45;

    /// <summary>Gets or sets the globally registered Bluetooth devices available to profile triggers.</summary>
    public List<BluetoothDeviceConfig> Devices { get; set; } = [];
}
