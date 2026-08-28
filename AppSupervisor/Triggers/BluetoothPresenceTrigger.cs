using AppSupervisor.Bluetooth;
using AppSupervisor.Core;

namespace AppSupervisor.Triggers;

/// <summary>Activates a profile while one globally registered Bluetooth device is present.</summary>
internal sealed class BluetoothPresenceTrigger : ITrigger
{
    private readonly string _deviceId;
    private readonly IBluetoothPresenceSource _presenceSource;

    internal BluetoothPresenceTrigger(
        string deviceId,
        IBluetoothPresenceSource presenceSource)
    {
        _deviceId = deviceId;
        _presenceSource = presenceSource;
    }

    /// <inheritdoc />
    public bool IsActive() => _presenceSource.IsPresent(_deviceId);
}
