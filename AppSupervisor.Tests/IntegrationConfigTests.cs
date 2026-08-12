using AppSupervisor.Configuration;
using AppSupervisor.Notifications;

namespace AppSupervisor.Tests;

/// <summary>Verifies the strict global integration configuration contract.</summary>
public sealed class IntegrationConfigTests
{
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
                                ModelNumber = " Tundra Tracker "
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
