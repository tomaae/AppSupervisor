using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace AppSupervisor.Bluetooth;

/// <summary>Discovers Bluetooth Classic and Low Energy association endpoints through Windows.</summary>
internal sealed class BluetoothDeviceScanner : IBluetoothDeviceScanner
{
    private static readonly string[] RequestedProperties =
    [
        "System.Devices.Aep.DeviceAddress",
        "System.Devices.Aep.IsConnected",
        "System.Devices.Aep.IsPresent"
    ];

    private readonly TimeSpan _discoveryDuration;

    internal BluetoothDeviceScanner(TimeSpan? discoveryDuration = null)
    {
        _discoveryDuration = discoveryDuration ?? TimeSpan.FromSeconds(10);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BluetoothDeviceSnapshot>> DiscoverAsync(
        CancellationToken cancellationToken)
    {
        var discovered = new ConcurrentDictionary<string, BluetoothDeviceSnapshot>(
            StringComparer.OrdinalIgnoreCase
        );
        BluetoothScanStatus[] statuses = await Task.WhenAll(
            ScanPairingStateAsync(
                BluetoothDeviceKind.Classic,
                paired: true,
                discovered,
                cancellationToken
            ),
            ScanPairingStateAsync(
                BluetoothDeviceKind.Classic,
                paired: false,
                discovered,
                cancellationToken
            ),
            ScanPairingStateAsync(
                BluetoothDeviceKind.LowEnergy,
                paired: true,
                discovered,
                cancellationToken
            ),
            ScanPairingStateAsync(
                BluetoothDeviceKind.LowEnergy,
                paired: false,
                discovered,
                cancellationToken
            )
        ).ConfigureAwait(false);

        if (discovered.IsEmpty && statuses.All(status => status == BluetoothScanStatus.Aborted))
        {
            throw new InvalidOperationException(
                "Windows aborted every Bluetooth Classic and Low Energy discovery watcher. " +
                "Check the Bluetooth adapter and its driver."
            );
        }

        return discovered.Values
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.Address, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.Kind)
            .ToArray();
    }

    private async Task<BluetoothScanStatus> ScanPairingStateAsync(
        BluetoothDeviceKind kind,
        bool paired,
        ConcurrentDictionary<string, BluetoothDeviceSnapshot> discovered,
        CancellationToken cancellationToken)
    {
        string selector = kind == BluetoothDeviceKind.Classic
            ? BluetoothDevice.GetDeviceSelectorFromPairingState(paired)
            : BluetoothLEDevice.GetDeviceSelectorFromPairingState(paired);
        DeviceWatcher watcher = DeviceInformation.CreateWatcher(
            selector,
            RequestedProperties,
            DeviceInformationKind.AssociationEndpoint
        );
        var completed = new TaskCompletionSource<BluetoothScanStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var resolutions = new ConcurrentBag<Task>();
        var observed = new ConcurrentDictionary<string, DeviceInformation>(
            StringComparer.OrdinalIgnoreCase
        );

        watcher.Added += (_, device) =>
        {
            observed[device.Id] = device;
            resolutions.Add(AddSnapshotAsync(kind, device, discovered));
        };
        watcher.Updated += (_, update) =>
        {
            try
            {
                if (!observed.TryGetValue(update.Id, out DeviceInformation? device))
                    return;

                device.Update(update);
                resolutions.Add(AddSnapshotAsync(kind, device, discovered));
            }
            catch (Exception exception)
            {
                SupervisorLog.WriteTrace(
                    $"Bluetooth discovery skipped update '{update.Id}': {exception.Message}"
                );
            }
        };
        watcher.Removed += (_, update) =>
        {
            observed.TryRemove(update.Id, out DeviceInformation? _);
        };
        watcher.Stopped += (_, _) => completed.TrySetResult(
            watcher.Status == DeviceWatcherStatus.Aborted
                ? BluetoothScanStatus.Aborted
                : BluetoothScanStatus.Completed
        );

        BluetoothScanStatus status;

        try
        {
            watcher.Start();
            Task duration = Task.Delay(_discoveryDuration, cancellationToken);
            Task winner = await Task.WhenAny(completed.Task, duration).ConfigureAwait(false);

            if (winner == completed.Task)
            {
                status = await completed.Task.ConfigureAwait(false);
            }
            else
            {
                await duration.ConfigureAwait(false);
                status = BluetoothScanStatus.Completed;
            }
        }
        finally
        {
            if (watcher.Status is
                DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            {
                watcher.Stop();
            }
        }

        if (!completed.Task.IsCompleted)
            await completed.Task.ConfigureAwait(false);

        Task[] pending = resolutions.ToArray();
        if (pending.Length > 0)
            await Task.WhenAll(pending).ConfigureAwait(false);

        return status;
    }

    private static async Task AddSnapshotAsync(
        BluetoothDeviceKind kind,
        DeviceInformation device,
        ConcurrentDictionary<string, BluetoothDeviceSnapshot> discovered)
    {
        try
        {
            string address = NormalizeAddress(ReadStringProperty(
                device,
                "System.Devices.Aep.DeviceAddress"
            ));
            bool isConnected = ReadBooleanProperty(
                device,
                "System.Devices.Aep.IsConnected"
            );
            bool isPresent = ReadBooleanProperty(
                device,
                "System.Devices.Aep.IsPresent"
            );

            if (address.Length == 0)
            {
                (address, isConnected) = await ResolveAddressAsync(
                    kind,
                    device.Id,
                    isConnected
                ).ConfigureAwait(false);
            }

            if (address.Length == 0)
                return;

            bool paired = device.Pairing.IsPaired;
            var snapshot = new BluetoothDeviceSnapshot(
                device.Id,
                string.IsNullOrWhiteSpace(device.Name) ? address : device.Name.Trim(),
                address,
                kind,
                paired,
                isConnected,
                isPresent || isConnected
            );
            discovered.AddOrUpdate(
                $"{kind}:{address}",
                snapshot,
                (_, existing) => existing with
                {
                    Name = snapshot.Name,
                    IsPaired = existing.IsPaired || snapshot.IsPaired,
                    IsConnected = existing.IsConnected || snapshot.IsConnected,
                    IsPresent = existing.IsPresent || snapshot.IsPresent
                }
            );
        }
        catch (Exception exception)
        {
            SupervisorLog.WriteTrace(
                $"Bluetooth discovery skipped device '{device.Id}': {exception.Message}"
            );
        }
    }

    private static async Task<(string Address, bool Connected)> ResolveAddressAsync(
        BluetoothDeviceKind kind,
        string deviceId,
        bool connected)
    {
        if (kind == BluetoothDeviceKind.Classic)
        {
            using BluetoothDevice? device = await BluetoothDevice.FromIdAsync(deviceId);
            return device is null
                ? ("", connected)
                : (FormatAddress(device.BluetoothAddress),
                    connected || device.ConnectionStatus == BluetoothConnectionStatus.Connected);
        }

        using BluetoothLEDevice? lowEnergyDevice = await BluetoothLEDevice.FromIdAsync(deviceId);
        return lowEnergyDevice is null
            ? ("", connected)
            : (FormatAddress(lowEnergyDevice.BluetoothAddress),
                connected || lowEnergyDevice.ConnectionStatus == BluetoothConnectionStatus.Connected);
    }

    internal static string NormalizeAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return "";

        string normalized = new(address.Where(Uri.IsHexDigit).ToArray());
        return normalized.Length == 12 ? normalized.ToUpperInvariant() : "";
    }

    private static string FormatAddress(ulong address) =>
        address.ToString("X12", System.Globalization.CultureInfo.InvariantCulture);

    private static string ReadStringProperty(DeviceInformation device, string key) =>
        device.Properties.TryGetValue(key, out object? value) ? value?.ToString() ?? "" : "";

    private static bool ReadBooleanProperty(DeviceInformation device, string key) =>
        device.Properties.TryGetValue(key, out object? value) && value is true;

    private enum BluetoothScanStatus
    {
        Completed,
        Aborted
    }
}
