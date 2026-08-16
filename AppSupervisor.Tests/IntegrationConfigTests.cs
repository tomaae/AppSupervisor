using AppSupervisor.Configuration;
using AppSupervisor.Notifications;

namespace AppSupervisor.Tests;

/// <summary>Verifies the strict global integration configuration contract.</summary>
public sealed class IntegrationConfigTests
{
    [Fact]
    public void SerializeAndLoad_SupervisorApi_RoundTripsGlobalToggle()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "config.json");

        try
        {
            ConfigFileWriter.SaveAtomic(path, new AppSupervisorConfig
            {
                Integrations = new IntegrationsConfig
                {
                    SupervisorApi = new SupervisorApiConfig { Enabled = true }
                }
            });

            Assert.True(ConfigLoader.Load(path).Integrations.SupervisorApi.Enabled);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SerializeAndLoad_SteamVrDevice_RoundTripsGlobalSettings()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "config.json");

        try
        {
            var configuration = new AppSupervisorConfig
            {
                Integrations = new IntegrationsConfig
                {
                    SteamVr = new SteamVrIntegrationConfig
                    {
                        Enabled = true,
                        ReminderIntervalMinutes = 5,
                        Devices =
                        [
                            new SteamVrDeviceConfig
                            {
                                SerialNumber = " LHR-TEST ",
                                Name = " Waist tracker ",
                                DeviceClass = SteamVrDeviceClass.GenericTracker,
                                ModelNumber = " Tundra Tracker ",
                                Role = SteamVrDeviceRole.LeftFoot
                            }
                        ],
                        Notifications = new NotificationConfig
                        {
                            Target = [NotificationTarget.XsOverlay]
                        }
                    }
                }
            };

            ConfigFileWriter.SaveAtomic(path, configuration);
            AppSupervisorConfig loaded = ConfigLoader.Load(path);

            SteamVrIntegrationConfig steamVr = loaded.Integrations.SteamVr;
            Assert.True(steamVr.Enabled);
            Assert.Equal(5, steamVr.ReminderIntervalMinutes);
            SteamVrDeviceConfig device = Assert.Single(steamVr.Devices);
            Assert.Equal("LHR-TEST", device.SerialNumber);
            Assert.Equal("Waist tracker", device.Name);
            Assert.Equal(SteamVrDeviceRole.LeftFoot, device.Role);
            Assert.Equal([NotificationTarget.XsOverlay], steamVr.Notifications.Target);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Validate_DuplicateSteamVrSerial_ThrowsValidationError()
    {
        var configuration = new IntegrationsConfig
        {
            SteamVr = new SteamVrIntegrationConfig
            {
                Devices =
                [
                    CreateDevice("LHR-DUPLICATE", "First"),
                    CreateDevice("lhr-duplicate", "Second")
                ]
            }
        };

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => IntegrationConfigValidator.Validate(configuration)
        );

        Assert.Contains("duplicated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SerializeAndLoad_HomeAssistantResource_RoundTripsGlobalAuthenticationAndAction()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "config.json");

        try
        {
            ConfigFileWriter.SaveAtomic(path, new AppSupervisorConfig
            {
                Integrations = new IntegrationsConfig
                {
                    HomeAssistant = new HomeAssistantIntegrationConfig
                    {
                        Url = " https://home-assistant.example:8123 ",
                        Token = " test-token "
                    }
                },
                Profiles =
                [
                    new SupervisorProfileConfig
                    {
                        Name = "Home Assistant",
                        MonitorProcess = "notepad.exe",
                        HomeAssistantResources =
                        [
                            new HomeAssistantResourceConfig
                            {
                                Service = "switch.turn_on",
                                EntityId = "switch.test",
                                EntityName = "Test switch",
                                VerifyStateChange = true,
                                Persistent = true,
                                Notifications = new NotificationConfig
                                {
                                    Target = [NotificationTarget.Windows]
                                }
                            }
                        ]
                    }
                ]
            });

            AppSupervisorConfig loaded = ConfigLoader.Load(path);
            HomeAssistantResourceConfig resource = Assert.Single(
                Assert.Single(loaded.Profiles).HomeAssistantResources
            );

            Assert.Equal("https://home-assistant.example:8123", loaded.Integrations.HomeAssistant.Url);
            Assert.Equal("test-token", loaded.Integrations.HomeAssistant.Token);
            Assert.Equal("switch.turn_on", resource.Service);
            Assert.Equal("switch.test", resource.EntityId);
            Assert.True(resource.VerifyStateChange);
            Assert.True(resource.Persistent);
            Assert.Equal([NotificationTarget.Windows], resource.Notifications.Target);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Validate_ButtonWithPersistence_ThrowsValidationError()
    {
        var profile = new SupervisorProfileConfig
        {
            Name = "Button",
            MonitorProcess = "notepad.exe",
            HomeAssistantResources =
            [
                new HomeAssistantResourceConfig
                {
                    Service = "button.press",
                    EntityId = "button.restart",
                    Persistent = true
                }
            ]
        };

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => ConfigValidator.Validate([profile])
        );

        Assert.Contains("stateless", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_EnabledProfilesTargetSameHomeAssistantEntity_ThrowsValidationError()
    {
        SupervisorProfileConfig CreateProfile(string name, string service) => new()
        {
            Name = name,
            MonitorProcess = $"{name}.exe",
            HomeAssistantResources =
            [
                new HomeAssistantResourceConfig
                {
                    Service = service,
                    EntityId = "switch.shared"
                }
            ]
        };

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => ConfigValidator.Validate(
            [
                CreateProfile("First", "switch.turn_on"),
                CreateProfile("Second", "switch.turn_off")
            ])
        );

        Assert.Contains("duplicates the Home Assistant entity", exception.Message);
    }

    private static SteamVrDeviceConfig CreateDevice(string serial, string name) => new()
    {
        SerialNumber = serial,
        Name = name,
        DeviceClass = SteamVrDeviceClass.GenericTracker
    };

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.IntegrationTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(path);
        return path;
    }
}
