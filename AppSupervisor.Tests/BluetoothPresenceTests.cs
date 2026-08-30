using AppSupervisor.Bluetooth;
using AppSupervisor.Triggers;

namespace AppSupervisor.Tests;

/// <summary>Verifies cached Bluetooth presence and profile trigger behavior.</summary>
public sealed class BluetoothPresenceTests
{
    [Fact]
    public void Trigger_UsesAnySelectedDevicePresence()
    {
        var source = new MutablePresenceSource(["second-id"]);
        var trigger = new BluetoothPresenceTrigger(["first-id", "second-id"], source);

        Assert.True(trigger.IsActive());
        Assert.Equal(["first-id", "second-id"], source.RequestedDeviceIds);

        source.PresentDeviceIds.Clear();

        Assert.False(trigger.IsActive());
    }

    [Fact]
    public async Task Trigger_RemainsActiveThroughDeviceHandoffUntilAllPresenceExpires()
    {
        BluetoothDeviceSnapshot first = CreatePresentSnapshot(
            "first-windows-id",
            "First",
            "AABBCCDDEE01"
        );
        BluetoothDeviceSnapshot second = CreatePresentSnapshot(
            "second-windows-id",
            "Second",
            "AABBCCDDEE02"
        );
        var scanner = new HandoffScanner(first, second);
        var configuration = new BluetoothIntegrationConfig
        {
            Devices =
            [
                CreateConfiguredDevice("first-id", "First", "AABBCCDDEE01"),
                CreateConfiguredDevice("second-id", "Second", "AABBCCDDEE02")
            ]
        };
        using var monitor = new BluetoothPresenceMonitor(
            configuration,
            scanner,
            scanInterval: TimeSpan.FromMilliseconds(15),
            presenceTimeout: TimeSpan.FromMilliseconds(100)
        );
        var trigger = new BluetoothPresenceTrigger(["first-id", "second-id"], monitor);

        await WaitUntilAsync(() => monitor.IsPresent("first-id"));
        await WaitUntilAsync(() => !monitor.IsPresent("first-id"));

        Assert.True(monitor.IsPresent("second-id"));
        Assert.True(trigger.IsActive());

        await WaitUntilAsync(() => !trigger.IsActive());
    }

    [Fact]
    public async Task Monitor_ObservedDeviceBecomesPresentThenExpires()
    {
        var scanner = new SequencedScanner(
        [
            new BluetoothDeviceSnapshot(
                "windows-id",
                "Phone",
                "AABBCCDDEEFF",
                BluetoothDeviceKind.LowEnergy,
                IsPaired: false,
                IsConnected: false,
                IsPresent: true
            )
        ]);
        var configuration = new BluetoothIntegrationConfig
        {
            Devices =
            [
                new BluetoothDeviceConfig
                {
                    DeviceId = "phone-id",
                    Name = "Phone",
                    Address = "AABBCCDDEEFF",
                    Kind = BluetoothDeviceKind.LowEnergy
                }
            ]
        };
        using var monitor = new BluetoothPresenceMonitor(
            configuration,
            scanner,
            scanInterval: TimeSpan.FromMilliseconds(15),
            presenceTimeout: TimeSpan.FromMilliseconds(80)
        );

        await WaitUntilAsync(() => monitor.IsPresent("phone-id"));
        await WaitUntilAsync(() => !monitor.IsPresent("phone-id"));

        Assert.True(scanner.CallCount >= 2);
    }

    [Fact]
    public async Task Monitor_NoEnabledProfileDeviceIds_DoesNotStartScanner()
    {
        var scanner = new SequencedScanner([]);
        var configuration = new BluetoothIntegrationConfig
        {
            Devices =
            [
                CreateConfiguredDevice("registered-id", "Registered", "AABBCCDDEEFF")
            ]
        };
        using var monitor = new BluetoothPresenceMonitor(
            configuration,
            scanner,
            scanInterval: TimeSpan.FromMilliseconds(10),
            monitoredDeviceIds: []
        );

        await Task.Delay(80);

        Assert.Equal(0, scanner.CallCount);
        Assert.False(monitor.IsPresent("registered-id"));
    }

    [Fact]
    public async Task Monitor_TracksOnlyDeviceIdsUsedByEnabledProfiles()
    {
        var scanner = new SequencedScanner(
        [
            CreatePresentSnapshot("selected-windows-id", "Selected", "AABBCCDDEE01"),
            CreatePresentSnapshot("unused-windows-id", "Unused", "AABBCCDDEE02")
        ]);
        var configuration = new BluetoothIntegrationConfig
        {
            Devices =
            [
                CreateConfiguredDevice("selected-id", "Selected", "AABBCCDDEE01"),
                CreateConfiguredDevice("unused-id", "Unused", "AABBCCDDEE02")
            ]
        };
        using var monitor = new BluetoothPresenceMonitor(
            configuration,
            scanner,
            scanInterval: TimeSpan.FromMilliseconds(15),
            presenceTimeout: TimeSpan.FromMilliseconds(100),
            monitoredDeviceIds: ["selected-id"]
        );

        await WaitUntilAsync(() => monitor.IsPresent("selected-id"));

        Assert.False(monitor.IsPresent("unused-id"));
    }

    [Fact]
    public void SelectMonitoredDeviceIds_UsesOnlyEnabledBluetoothProfiles()
    {
        SupervisorProfileConfig[] profiles =
        [
            new()
            {
                Enabled = true,
                TriggerType = ProfileTriggerType.BluetoothDevice,
                MonitorBluetoothDeviceIds = ["first-id", "second-id"]
            },
            new()
            {
                Enabled = false,
                TriggerType = ProfileTriggerType.BluetoothDevice,
                MonitorBluetoothDeviceIds = ["disabled-id"]
            },
            new()
            {
                Enabled = true,
                TriggerType = ProfileTriggerType.Process,
                MonitorBluetoothDeviceIds = ["process-id"]
            },
            new()
            {
                Enabled = true,
                TriggerType = ProfileTriggerType.BluetoothDevice,
                MonitorBluetoothDeviceIds = ["SECOND-ID"]
            }
        ];

        Assert.Equal(
            ["first-id", "second-id"],
            BluetoothPresenceMonitor.SelectMonitoredDeviceIds(profiles)
        );
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF", "AABBCCDDEEFF")]
    [InlineData("aa-bb-cc-dd-ee-ff", "AABBCCDDEEFF")]
    [InlineData("invalid", "")]
    public void NormalizeAddress_UsesAdapterIndependentHexadecimalForm(
        string input,
        string expected)
    {
        Assert.Equal(expected, BluetoothDeviceScanner.NormalizeAddress(input));
    }

    [Theory]
    [InlineData("4C2299AC31BA", "4C2299AC31BA")]
    [InlineData("4C:22:99:AC:31:BA", "4C2299AC31BA")]
    [InlineData(" 4c-22-99-ac-31-ba ", "4C2299AC31BA")]
    [InlineData("Bluetooth 4c:22:99:ac:31:ba", "4C2299AC31BA")]
    [InlineData("Bluetooth LE 4c:22:99:ac:31:ba", "4C2299AC31BA")]
    public void SelectDisplayName_RejectsAddressPlaceholders(string name, string address)
    {
        Assert.Equal("", BluetoothDeviceScanner.SelectDisplayName(address, name));
        Assert.False(BluetoothDeviceScanner.HasUsableName(name, address));
    }

    [Fact]
    public void SelectDisplayName_UsesFirstHumanReadableCandidate()
    {
        Assert.Equal(
            "Pixel Buds",
            BluetoothDeviceScanner.SelectDisplayName(
                "4C2299AC31BA",
                "4C:22:99:AC:31:BA",
                "  Pixel Buds  ",
                "Fallback"
            )
        );
    }

    [Fact]
    public void ChoosePreferredName_PreservesEditedNameAndUpgradesAddressFallback()
    {
        const string address = "4C2299AC31BA";

        Assert.Equal(
            "My headset",
            BluetoothDeviceScanner.ChoosePreferredName("My headset", "Headset", address)
        );
        Assert.Equal(
            "Headset",
            BluetoothDeviceScanner.ChoosePreferredName(address, "Headset", address)
        );
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime timeoutUtc = DateTime.UtcNow.AddSeconds(3);

        while (!condition())
        {
            if (DateTime.UtcNow >= timeoutUtc)
                throw new TimeoutException("Bluetooth presence state did not change in time.");

            await Task.Delay(10);
        }
    }

    private static BluetoothDeviceSnapshot CreatePresentSnapshot(
        string windowsId,
        string name,
        string address) => new(
            windowsId,
            name,
            address,
            BluetoothDeviceKind.LowEnergy,
            IsPaired: false,
            IsConnected: false,
            IsPresent: true
        );

    private static BluetoothDeviceConfig CreateConfiguredDevice(
        string deviceId,
        string name,
        string address) => new()
        {
            DeviceId = deviceId,
            Name = name,
            Address = address,
            Kind = BluetoothDeviceKind.LowEnergy
        };

    private sealed class MutablePresenceSource(IEnumerable<string> presentDeviceIds)
        : IBluetoothPresenceSource
    {
        public HashSet<string> PresentDeviceIds { get; } =
            new(presentDeviceIds, StringComparer.OrdinalIgnoreCase);
        public List<string> RequestedDeviceIds { get; } = [];

        public bool IsPresent(string deviceId)
        {
            RequestedDeviceIds.Add(deviceId);
            return PresentDeviceIds.Contains(deviceId);
        }
    }

    private sealed class HandoffScanner(
        BluetoothDeviceSnapshot first,
        BluetoothDeviceSnapshot second) : IBluetoothDeviceScanner
    {
        private int _callCount;

        public Task<IReadOnlyList<BluetoothDeviceSnapshot>> DiscoverAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int call = Interlocked.Increment(ref _callCount);
            IReadOnlyList<BluetoothDeviceSnapshot> result = call switch
            {
                1 => [first],
                <= 10 => [second],
                _ => []
            };
            return Task.FromResult(result);
        }
    }

    private sealed class SequencedScanner(
        IReadOnlyList<BluetoothDeviceSnapshot> firstResult) : IBluetoothDeviceScanner
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<IReadOnlyList<BluetoothDeviceSnapshot>> DiscoverAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int call = Interlocked.Increment(ref _callCount);
            return Task.FromResult(call == 1 ? firstResult : []);
        }
    }
}
