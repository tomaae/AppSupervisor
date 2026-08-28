namespace AppSupervisor.Bluetooth;

/// <summary>Provides cached, nonblocking Bluetooth presence state to profile triggers.</summary>
internal interface IBluetoothPresenceSource
{
    /// <summary>Returns whether the registered device was observed within its presence timeout.</summary>
    bool IsPresent(string deviceId);
}
