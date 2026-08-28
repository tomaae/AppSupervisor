namespace AppSupervisor.Bluetooth;

/// <summary>Discovers nearby and paired Bluetooth devices without retaining adapter state.</summary>
internal interface IBluetoothDeviceScanner
{
    /// <summary>Runs one bounded Classic and Low Energy discovery cycle.</summary>
    Task<IReadOnlyList<BluetoothDeviceSnapshot>> DiscoverAsync(
        CancellationToken cancellationToken);
}
