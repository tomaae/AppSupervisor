using AppSupervisor.Configuration;
using AppSupervisor.Bluetooth;
using System.Text.Json;

namespace AppSupervisor.Tests;

/// <summary>Verifies global Bluetooth registration and profile-reference validation.</summary>
public sealed class BluetoothIntegrationTests
{
    [Fact]
    public void SerializeAndLoad_BluetoothProfile_RoundTripsAndNormalizesAddress()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "config.json");
        const string deviceId = "phone-id";
        const string secondDeviceId = "tag-id";

        try
        {
            ConfigFileWriter.SaveAtomic(path, new AppSupervisorConfig
            {
                Integrations = new IntegrationsConfig
                {
                    Bluetooth = new BluetoothIntegrationConfig
                    {
                        ScanIntervalSeconds = 20,
                        PresenceTimeoutSeconds = 60,
                        Devices =
                        [
                            new BluetoothDeviceConfig
                            {
                                DeviceId = deviceId,
                                Name = " Phone ",
                                Address = "AA:BB:CC:DD:EE:FF",
                                Kind = BluetoothDeviceKind.LowEnergy,
                                ManufacturerName = " Microsoft ",
                                ManufacturerCompanyIds = [76, 6, 76]
                            },
                            new BluetoothDeviceConfig
                            {
                                DeviceId = secondDeviceId,
                                Name = "Tag",
                                Address = "112233445566",
                                Kind = BluetoothDeviceKind.LowEnergy
                            }
                        ]
                    }
                },
                Profiles =
                [
                    new SupervisorProfileConfig
                    {
                        Name = "Nearby phone",
                        TriggerType = ProfileTriggerType.BluetoothDevice,
                        MonitorBluetoothDeviceIds = [$" {deviceId} ", secondDeviceId]
                    }
                ]
            });

            AppSupervisorConfig loaded = ConfigLoader.Load(path);
            BluetoothDeviceConfig device = loaded.Integrations.Bluetooth.Devices[0];
            SupervisorProfileConfig profile = Assert.Single(loaded.Profiles);

            Assert.Equal("Phone", device.Name);
            Assert.Equal("AABBCCDDEEFF", device.Address);
            Assert.Equal(BluetoothDeviceKind.LowEnergy, device.Kind);
            Assert.Equal("Microsoft", device.ManufacturerName);
            Assert.Equal([6, 76], device.ManufacturerCompanyIds);
            Assert.Equal(ProfileTriggerType.BluetoothDevice, profile.TriggerType);
            Assert.Equal([deviceId, secondDeviceId], profile.MonitorBluetoothDeviceIds);
            Assert.Equal(20, loaded.Integrations.Bluetooth.ScanIntervalSeconds);
            Assert.Equal(60, loaded.Integrations.Bluetooth.PresenceTimeoutSeconds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_LegacySingularBluetoothTrigger_MigratesToDeviceList()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "config.json");

        try
        {
            File.WriteAllText(path, """
                {
                  "profiles": [
                    {
                      "name": "Legacy Bluetooth",
                      "triggerType": "bluetoothDevice",
                      "monitorBluetoothDeviceId": " phone-id "
                    }
                  ],
                  "integrations": {
                    "bluetooth": {
                      "devices": [
                        {
                          "deviceId": "phone-id",
                          "name": "Phone",
                          "address": "AABBCCDDEEFF",
                          "kind": "lowEnergy"
                        }
                      ]
                    }
                  }
                }
                """);

            AppSupervisorConfig loaded = ConfigLoader.Load(path);
            SupervisorProfileConfig profile = Assert.Single(loaded.Profiles);

            Assert.Equal(["phone-id"], profile.MonitorBluetoothDeviceIds);
            Assert.Null(profile.LegacyMonitorBluetoothDeviceId);

            using JsonDocument saved = JsonDocument.Parse(ConfigFileWriter.Serialize(loaded));
            JsonElement savedProfile = Assert.Single(
                saved.RootElement.GetProperty("profiles").EnumerateArray()
            );
            Assert.False(savedProfile.TryGetProperty("monitorBluetoothDeviceId", out _));
            Assert.Equal(
                ["phone-id"],
                savedProfile.GetProperty("monitorBluetoothDeviceIds")
                    .EnumerateArray()
                    .Select(item => item.GetString()!)
                    .ToArray()
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CompanyIdentifiers_KnownAndUnknownValues_AreFormattedAsHints()
    {
        Assert.Equal("Apple, Inc.", BluetoothCompanyIdentifiers.Format([76]));
        Assert.Equal("Company ID 0xEA60", BluetoothCompanyIdentifiers.Format([60000]));
        Assert.Equal("—", BluetoothCompanyIdentifiers.Format([]));
    }

    [Fact]
    public void MergeManufacturerCompanyIds_DeduplicatesAndSortsValues()
    {
        Assert.Equal(
            [6, 76, 117],
            BluetoothDeviceScanner.MergeManufacturerCompanyIds([76, 6], [117, 76])
        );
    }

    [Theory]
    [InlineData(-35, -35)]
    [InlineData(-126, -126)]
    [InlineData(-127, null)]
    [InlineData(0, null)]
    [InlineData(75, null)]
    [InlineData("-68", -68)]
    [InlineData("invalid", null)]
    [InlineData(null, null)]
    public void NormalizeSignalStrength_AcceptsOnlyPlausibleNegativeDbm(
        object? value,
        int? expected)
    {
        short? actual = BluetoothDeviceScanner.NormalizeSignalStrength(value);
        Assert.Equal(expected, actual is short signal ? (int?)signal : null);
    }

    [Fact]
    public void ChoosePreferredManufacturer_PreservesExistingWindowsMetadata()
    {
        Assert.Equal(
            "Existing vendor",
            BluetoothDeviceScanner.ChoosePreferredManufacturer(
                " Existing vendor ",
                "New vendor"
            )
        );
        Assert.Equal(
            "New vendor",
            BluetoothDeviceScanner.ChoosePreferredManufacturer("", " New vendor ")
        );
    }

    [Fact]
    public void Validate_BluetoothProfileReferencesMissingDevice_ThrowsValidationError()
    {
        var configuration = new AppSupervisorConfig
        {
            Profiles =
            [
                new SupervisorProfileConfig
                {
                    Name = "Missing device",
                    TriggerType = ProfileTriggerType.BluetoothDevice,
                    MonitorBluetoothDeviceIds = ["missing", "also-missing"]
                }
            ]
        };

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => ConfigFileWriter.Serialize(configuration)
        );

        Assert.Contains("not registered globally", exception.Message);
    }

    [Fact]
    public void Validate_DuplicateBluetoothAddress_ThrowsValidationError()
    {
        var configuration = new IntegrationsConfig
        {
            Bluetooth = new BluetoothIntegrationConfig
            {
                Devices =
                [
                    CreateDevice("first", "AA:BB:CC:DD:EE:FF"),
                    CreateDevice("second", "aabbccddeeff")
                ]
            }
        };

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => IntegrationConfigValidator.Validate(configuration)
        );

        Assert.Contains("address", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("duplicated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_ProcessProfile_DoesNotRequireBluetoothRegistration()
    {
        ConfigFileWriter.Serialize(new AppSupervisorConfig
        {
            Profiles =
            [
                new SupervisorProfileConfig
                {
                    Name = "Process profile",
                    MonitorProcess = "notepad.exe"
                }
            ]
        });
    }

    [Fact]
    public void Factory_BluetoothProfileUsesRegisteredDeviceNameAndPresence()
    {
        const string deviceId = "phone-id";
        const string secondDeviceId = "tag-id";
        var bluetooth = new BluetoothIntegrationConfig
        {
            Devices =
            [
                CreateDevice(deviceId, "AABBCCDDEEFF"),
                CreateDevice(secondDeviceId, "112233445566")
            ]
        };
        var source = new StubPresenceSource(secondDeviceId);
        using var profile = SupervisorProfileFactory.Create(
            new SupervisorProfileConfig
            {
                Name = "Nearby phone",
                TriggerType = ProfileTriggerType.BluetoothDevice,
                MonitorBluetoothDeviceIds = [deviceId, secondDeviceId]
            },
            _ => null,
            new HomeAssistantIntegrationConfig(),
            new MqttIntegrationConfig(),
            new ObsIntegrationConfig(),
            new TwitchIntegrationConfig(),
            bluetooth,
            source
        );

        profile.Update();

        Assert.True(profile.TriggerActive);
        Assert.Equal($"{deviceId} OR {secondDeviceId}", profile.TriggerDisplayName);
        Assert.Equal([deviceId, secondDeviceId], source.RequestedDeviceIds);
    }

    private static BluetoothDeviceConfig CreateDevice(string id, string address) => new()
    {
        DeviceId = id,
        Name = id,
        Address = address,
        Kind = BluetoothDeviceKind.Classic
    };

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.BluetoothIntegrationTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class StubPresenceSource(string presentDeviceId) : IBluetoothPresenceSource
    {
        public List<string> RequestedDeviceIds { get; } = [];

        public bool IsPresent(string deviceId)
        {
            RequestedDeviceIds.Add(deviceId);
            return string.Equals(deviceId, presentDeviceId, StringComparison.Ordinal);
        }
    }
}
