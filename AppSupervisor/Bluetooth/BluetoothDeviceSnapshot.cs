namespace AppSupervisor.Bluetooth;

/// <summary>Describes one Bluetooth device observed by a discovery cycle.</summary>
internal sealed record BluetoothDeviceSnapshot(
    string WindowsDeviceId,
    string Name,
    string Address,
    BluetoothDeviceKind Kind,
    bool IsPaired,
    bool IsConnected,
    bool IsPresent);
