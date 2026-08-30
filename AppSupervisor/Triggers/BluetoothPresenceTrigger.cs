using AppSupervisor.Bluetooth;
using AppSupervisor.Core;

namespace AppSupervisor.Triggers;

/// <summary>Activates a profile while any selected globally registered Bluetooth device is present.</summary>
internal sealed class BluetoothPresenceTrigger : ITrigger
{
    private readonly IReadOnlyList<string> _deviceIds;
    private readonly IBluetoothPresenceSource _presenceSource;

    internal BluetoothPresenceTrigger(
        IReadOnlyList<string> deviceIds,
        IBluetoothPresenceSource presenceSource)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        if (deviceIds.Count == 0)
            throw new ArgumentException("At least one Bluetooth device is required.", nameof(deviceIds));

        _deviceIds = deviceIds.ToArray();
        _presenceSource = presenceSource;
    }

    /// <inheritdoc />
    public bool IsActive() => _deviceIds.Any(_presenceSource.IsPresent);
}
