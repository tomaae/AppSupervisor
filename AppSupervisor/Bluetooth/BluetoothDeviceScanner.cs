using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace AppSupervisor.Bluetooth;

/// <summary>Discovers Bluetooth Classic and Low Energy association endpoints through Windows.</summary>
internal sealed class BluetoothDeviceScanner : IBluetoothDeviceScanner
{
    private static readonly string[] RequestedProperties =
    [
        "System.Devices.Aep.DeviceAddress",
        "System.Devices.Aep.Bluetooth.Le.AddressType",
        "System.Devices.Aep.IsConnected",
        "System.Devices.Aep.IsPresent",
        "System.Devices.FriendlyName",
        "System.ItemNameDisplay"
    ];

    private readonly TimeSpan _discoveryDuration;
    private readonly bool _resolveNames;

    internal BluetoothDeviceScanner(
        TimeSpan? discoveryDuration = null,
        bool resolveNames = true)
    {
        _discoveryDuration = discoveryDuration ?? TimeSpan.FromSeconds(10);
        _resolveNames = resolveNames;
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
            ScanningMode = BluetoothLEScanningMode.Active,
            AllowExtendedAdvertisements = true
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

    private async Task AddSnapshotAsync(
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

            if (address.Length == 0 || (_resolveNames && paired && name.Length == 0))
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

            if (_resolveNames && kind == BluetoothDeviceKind.LowEnergy && name.Length == 0)
            {
                (isConnected, string addressName) =
                    await ResolveLowEnergyDeviceByAddressAsync(
                        address,
                        ReadBluetoothAddressType(device),
                        isConnected
                    ).ConfigureAwait(false);
                name = SelectDisplayName(address, addressName);
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
                SelectDisplayName(address, ReadAdvertisementName(advertisement.Advertisement)),
                address,
                BluetoothDeviceKind.LowEnergy,
                IsPaired: false,
                IsConnected: false,
                IsPresent: true,
                SignalStrengthDbm: advertisement.RawSignalStrengthInDBm,
                ManufacturerCompanyIds: ReadManufacturerCompanyIds(
                    advertisement.Advertisement
                )
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
                IsPresent = existing.IsPresent || snapshot.IsPresent,
                SignalStrengthDbm = snapshot.SignalStrengthDbm ?? existing.SignalStrengthDbm,
                ManufacturerCompanyIds = MergeManufacturerCompanyIds(
                    existing.ManufacturerCompanyIds,
                    snapshot.ManufacturerCompanyIds
                )
            }
        );
    }

    private static IReadOnlyList<ushort> ReadManufacturerCompanyIds(
        BluetoothLEAdvertisement advertisement) => advertisement.ManufacturerData
            .Select(data => data.CompanyId)
            .Distinct()
            .OrderBy(companyId => companyId)
            .ToArray();

    internal static IReadOnlyList<ushort> MergeManufacturerCompanyIds(
        IReadOnlyList<ushort>? first,
        IReadOnlyList<ushort>? second) => (first ?? [])
            .Concat(second ?? [])
            .Distinct()
            .OrderBy(companyId => companyId)
            .ToArray();

    private static async Task<(bool Connected, string Name)>
        ResolveLowEnergyDeviceByAddressAsync(
            string address,
            BluetoothAddressType addressType,
            bool connected)
    {
        if (!ulong.TryParse(address, NumberStyles.HexNumber, CultureInfo.InvariantCulture,
            out ulong bluetoothAddress))
        {
            return (connected, "");
        }

        try
        {
            using BluetoothLEDevice? device = await BluetoothLEDevice.FromBluetoothAddressAsync(
                bluetoothAddress,
                addressType
            );
            return device is null
                ? (connected, "")
                : (
                    connected ||
                        device.ConnectionStatus == BluetoothConnectionStatus.Connected,
                    device.Name
                );
        }
        catch (Exception exception)
        {
            SupervisorLog.WriteTrace(
                $"Bluetooth discovery could not resolve LE device '{address}' by address: " +
                    exception.Message
            );
            return (connected, "");
        }
    }

    private static BluetoothAddressType ReadBluetoothAddressType(DeviceInformation device)
    {
        if (!device.Properties.TryGetValue(
                "System.Devices.Aep.Bluetooth.Le.AddressType",
                out object? value
            ))
        {
            return BluetoothAddressType.Unspecified;
        }

        try
        {
            return Convert.ToByte(value, CultureInfo.InvariantCulture) == 1
                ? BluetoothAddressType.Random
                : BluetoothAddressType.Public;
        }
        catch (Exception)
        {
            return BluetoothAddressType.Unspecified;
        }
    }

    private static string ReadAdvertisementName(BluetoothLEAdvertisement advertisement)
    {
        if (!string.IsNullOrWhiteSpace(advertisement.LocalName))
            return advertisement.LocalName;

        foreach (byte dataType in new[]
                 {
                     BluetoothLEAdvertisementDataTypes.CompleteLocalName,
                     BluetoothLEAdvertisementDataTypes.ShortenedLocalName
                 })
        {
            foreach (BluetoothLEAdvertisementDataSection section in
                     advertisement.GetSectionsByType(dataType))
            {
                try
                {
                    using DataReader reader = DataReader.FromBuffer(section.Data);
                    var bytes = new byte[section.Data.Length];
                    reader.ReadBytes(bytes);
                    string name = Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();
                    if (name.Length > 0)
                        return name;
                }
                catch (Exception exception)
                {
                    SupervisorLog.WriteTrace(
                        $"Bluetooth discovery could not decode an advertised name: " +
                            exception.Message
                    );
                }
            }
        }

        return "";
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

            if (normalizedAddress.Length > 0 &&
                (HasAddressSuffix(trimmed, "Bluetooth ", normalizedAddress) ||
                 HasAddressSuffix(trimmed, "Bluetooth LE ", normalizedAddress)))
            {
                continue;
            }

            return trimmed;
        }

        return "";
    }

    private static bool HasAddressSuffix(
        string candidate,
        string prefix,
        string normalizedAddress) =>
        candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            NormalizeAddress(candidate[prefix.Length..]),
            normalizedAddress,
            StringComparison.Ordinal
        );

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
