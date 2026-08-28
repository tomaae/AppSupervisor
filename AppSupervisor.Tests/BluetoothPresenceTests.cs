using AppSupervisor.Bluetooth;
using AppSupervisor.Triggers;

namespace AppSupervisor.Tests;

/// <summary>Verifies cached Bluetooth presence and profile trigger behavior.</summary>
public sealed class BluetoothPresenceTests
{
    [Fact]
    public void Trigger_DelegatesRegisteredDeviceLookupToPresenceSource()
    {
        var source = new StubPresenceSource("device-id");
        var trigger = new BluetoothPresenceTrigger("device-id", source);

        Assert.True(trigger.IsActive());
        Assert.Equal("device-id", source.LastRequestedDeviceId);
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

    private sealed class StubPresenceSource(string presentDeviceId) : IBluetoothPresenceSource
    {
        public string? LastRequestedDeviceId { get; private set; }

        public bool IsPresent(string deviceId)
        {
            LastRequestedDeviceId = deviceId;
            return string.Equals(deviceId, presentDeviceId, StringComparison.Ordinal);
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
