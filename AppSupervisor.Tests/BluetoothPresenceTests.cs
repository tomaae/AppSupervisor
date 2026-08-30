using AppSupervisor.Bluetooth;
using AppSupervisor.Triggers;
using System.Threading.Channels;

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
        var clock = new ManualTimeProvider();
        var scanner = new ControlledScanner();
        scanner.AddResult(first);
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
            scanInterval: TimeSpan.Zero,
            presenceTimeout: TimeSpan.FromSeconds(100),
            timeProvider: clock
        );
        var trigger = new BluetoothPresenceTrigger(["first-id", "second-id"], monitor);

        await WaitUntilAsync(() => monitor.IsPresent("first-id"));
        clock.Advance(TimeSpan.FromSeconds(60));
        scanner.AddResult(second);
        await WaitUntilAsync(() => monitor.IsPresent("second-id"));

        // Advance independently of runner scheduling: A expires while B is still fresh.
        clock.Advance(TimeSpan.FromSeconds(50));

        Assert.False(monitor.IsPresent("first-id"));
        Assert.True(monitor.IsPresent("second-id"));
        Assert.True(trigger.IsActive());

        clock.Advance(TimeSpan.FromSeconds(51));

        Assert.False(trigger.IsActive());
    }

    [Fact]
    public async Task Monitor_ObservedDeviceBecomesPresentThenExpires()
    {
        var clock = new ManualTimeProvider();
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
            presenceTimeout: TimeSpan.FromMilliseconds(80),
            timeProvider: clock
        );

        await WaitUntilAsync(() => monitor.IsPresent("phone-id"));
        clock.Advance(TimeSpan.FromMilliseconds(80));

        Assert.True(monitor.IsPresent("phone-id"));

        clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.False(monitor.IsPresent("phone-id"));
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
            monitoredDeviceIds: ["selected-id"],
            timeProvider: new ManualTimeProvider()
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

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _utcTicks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).Ticks;

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        internal void Advance(TimeSpan duration) => Interlocked.Add(ref _utcTicks, duration.Ticks);
    }

    private sealed class ControlledScanner : IBluetoothDeviceScanner
    {
        private readonly Channel<IReadOnlyList<BluetoothDeviceSnapshot>> _results =
            Channel.CreateUnbounded<IReadOnlyList<BluetoothDeviceSnapshot>>();

        internal void AddResult(params BluetoothDeviceSnapshot[] devices) =>
            _results.Writer.TryWrite(devices);

        public async Task<IReadOnlyList<BluetoothDeviceSnapshot>> DiscoverAsync(
            CancellationToken cancellationToken) =>
            await _results.Reader.ReadAsync(cancellationToken);
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
