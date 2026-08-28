using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;

namespace AppSupervisor.Bluetooth;

/// <summary>Discovers Bluetooth Classic and Low Energy association endpoints through Windows.</summary>
internal sealed class BluetoothDeviceScanner : IBluetoothDeviceScanner
{
    private static readonly string[] RequestedProperties =
    [
        "System.Devices.Aep.DeviceAddress",
        "System.Devices.Aep.IsConnected",
        "System.Devices.Aep.IsPresent",
        "System.Devices.FriendlyName",
        "System.ItemNameDisplay"
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
            ),
            ScanAdvertisementsAsync(discovered, cancellationToken)
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

    private async Task<BluetoothScanStatus> ScanAdvertisementsAsync(
        ConcurrentDictionary<string, BluetoothDeviceSnapshot> discovered,
        CancellationToken cancellationToken)
    {
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            // Active scanning requests scan responses, which are where many peripherals
            // publish their shortened or complete local name.
            ScanningMode = BluetoothLEScanningMode.Active
        };
        var completed = new TaskCompletionSource<BluetoothScanStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        watcher.Received += (_, advertisement) =>
            AddAdvertisementSnapshot(advertisement, discovered);
        watcher.Stopped += (_, _) => completed.TrySetResult(
            watcher.Status == BluetoothLEAdvertisementWatcherStatus.Aborted
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
            if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
                watcher.Stop();
        }

        if (!completed.Task.IsCompleted)
            await completed.Task.ConfigureAwait(false);

        return status;
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
            bool paired = device.Pairing.IsPaired;
            string name = SelectDisplayName(
                address,
                ReadStringProperty(device, "System.Devices.FriendlyName"),
                device.Name,
                ReadStringProperty(device, "System.ItemNameDisplay")
            );

            if (address.Length == 0 || (paired && name.Length == 0))
            {
                (string resolvedAddress, bool resolvedConnected, string resolvedName) =
                    await ResolveDeviceAsync(
                        kind,
                        device.Id,
                        isConnected
                    ).ConfigureAwait(false);
                address = address.Length == 0 ? resolvedAddress : address;
                isConnected = resolvedConnected;
                name = SelectDisplayName(address, name, resolvedName);
            }

            if (address.Length == 0)
                return;

            var snapshot = new BluetoothDeviceSnapshot(
                device.Id,
                name,
                address,
                kind,
                paired,
                isConnected,
                isPresent || isConnected
            );
            MergeSnapshot(discovered, snapshot);
        }
        catch (Exception exception)
        {
            SupervisorLog.WriteTrace(
                $"Bluetooth discovery skipped device '{device.Id}': {exception.Message}"
            );
        }
    }

    private static void AddAdvertisementSnapshot(
        BluetoothLEAdvertisementReceivedEventArgs advertisement,
        ConcurrentDictionary<string, BluetoothDeviceSnapshot> discovered)
    {
        try
        {
            // -127 is Windows' synthetic out-of-range notification, not a live sighting.
            if (advertisement.RawSignalStrengthInDBm <= -127 || advertisement.BluetoothAddress == 0)
                return;

            string address = FormatAddress(advertisement.BluetoothAddress);
            var snapshot = new BluetoothDeviceSnapshot(
                $"ble-advertisement:{address}",
                SelectDisplayName(address, advertisement.Advertisement.LocalName),
                address,
                BluetoothDeviceKind.LowEnergy,
                IsPaired: false,
                IsConnected: false,
                IsPresent: true
            );
            MergeSnapshot(discovered, snapshot);
        }
        catch (Exception exception)
        {
            SupervisorLog.WriteTrace(
                $"Bluetooth discovery skipped an LE advertisement: {exception.Message}"
            );
        }
    }

    private static void MergeSnapshot(
        ConcurrentDictionary<string, BluetoothDeviceSnapshot> discovered,
        BluetoothDeviceSnapshot snapshot)
    {
        discovered.AddOrUpdate(
            $"{snapshot.Kind}:{snapshot.Address}",
            snapshot,
            (_, existing) => existing with
            {
                WindowsDeviceId = existing.WindowsDeviceId.StartsWith(
                    "ble-advertisement:",
                    StringComparison.Ordinal
                ) ? snapshot.WindowsDeviceId : existing.WindowsDeviceId,
                Name = ChoosePreferredName(existing.Name, snapshot.Name, snapshot.Address),
                IsPaired = existing.IsPaired || snapshot.IsPaired,
                IsConnected = existing.IsConnected || snapshot.IsConnected,
                IsPresent = existing.IsPresent || snapshot.IsPresent
            }
        );
    }

    private static async Task<(string Address, bool Connected, string Name)> ResolveDeviceAsync(
        BluetoothDeviceKind kind,
        string deviceId,
        bool connected)
    {
        if (kind == BluetoothDeviceKind.Classic)
        {
            using BluetoothDevice? device = await BluetoothDevice.FromIdAsync(deviceId);
            return device is null
                ? ("", connected, "")
                : (FormatAddress(device.BluetoothAddress),
                    connected || device.ConnectionStatus == BluetoothConnectionStatus.Connected,
                    device.Name);
        }

        using BluetoothLEDevice? lowEnergyDevice = await BluetoothLEDevice.FromIdAsync(deviceId);
        return lowEnergyDevice is null
            ? ("", connected, "")
            : (FormatAddress(lowEnergyDevice.BluetoothAddress),
                connected || lowEnergyDevice.ConnectionStatus == BluetoothConnectionStatus.Connected,
                lowEnergyDevice.Name);
    }

    internal static bool HasUsableName(string? name, string address) =>
        SelectDisplayName(address, name).Length > 0;

    internal static string ChoosePreferredName(
        string? existingName,
        string? candidateName,
        string address)
    {
        string existing = SelectDisplayName(address, existingName);
        return existing.Length > 0 ? existing : SelectDisplayName(address, candidateName);
    }

    internal static string SelectDisplayName(string address, params string?[] candidates)
    {
        string normalizedAddress = NormalizeAddress(address);

        foreach (string? candidate in candidates)
        {
            string trimmed = candidate?.Trim() ?? "";
            if (trimmed.Length == 0)
                continue;

            string normalizedCandidate = NormalizeAddress(trimmed);
            if (normalizedAddress.Length > 0 &&
                string.Equals(normalizedCandidate, normalizedAddress, StringComparison.Ordinal))
            {
                continue;
            }

            return trimmed;
        }

        return "";
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
