namespace AppSupervisor.Bluetooth;

/// <summary>Provides an always-absent source for process-only factory callers.</summary>
internal sealed class NullBluetoothPresenceSource : IBluetoothPresenceSource
{
    internal static NullBluetoothPresenceSource Instance { get; } = new();

    private NullBluetoothPresenceSource()
    {
    }

    public bool IsPresent(string deviceId) => false;
}
