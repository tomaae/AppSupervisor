namespace AppSupervisor;

/// <summary>Identifies the condition that activates a supervisor profile.</summary>
public enum ProfileTriggerType
{
    /// <summary>Activate while a configured executable is running.</summary>
    Process,

    /// <summary>Activate while a globally registered Bluetooth device is present.</summary>
    BluetoothDevice
}
