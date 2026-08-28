namespace AppSupervisor;

/// <summary>Identifies one globally registered Bluetooth device independently of the local adapter.</summary>
public sealed class BluetoothDeviceConfig
{
    /// <summary>Gets or sets the stable AppSupervisor identifier referenced by profiles.</summary>
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets or sets the user-facing device name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Gets or sets the remote 48-bit Bluetooth address as twelve hexadecimal digits.</summary>
    public string Address { get; set; } = "";

    /// <summary>Gets or sets the transport used to detect the device.</summary>
    public BluetoothDeviceKind Kind { get; set; }
}
