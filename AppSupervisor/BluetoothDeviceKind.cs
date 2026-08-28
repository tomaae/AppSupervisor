namespace AppSupervisor;

/// <summary>Identifies the Bluetooth transport used to discover a registered device.</summary>
public enum BluetoothDeviceKind
{
    /// <summary>Bluetooth Classic device discovered through the BR/EDR transport.</summary>
    Classic,

    /// <summary>Bluetooth Low Energy device discovered through advertisements.</summary>
    LowEnergy
}
