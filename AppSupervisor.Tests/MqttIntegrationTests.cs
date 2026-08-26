using AppSupervisor.Configuration;
using AppSupervisor.Notifications;

namespace AppSupervisor.Tests;

/// <summary>Verifies MQTT configuration persistence and fail-safe validation.</summary>
public sealed class MqttIntegrationTests
{
    [Fact]
    public void SerializeAndLoad_ReversiblePublish_PreservesMessagesAndNormalizesHost()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "config.json");

        try
        {
            ConfigFileWriter.SaveAtomic(path, new AppSupervisorConfig
            {
                Integrations = new IntegrationsConfig
                {
                    Mqtt = new MqttIntegrationConfig
                    {
                        Host = " broker.example.test ",
                        Port = 8883,
                        UseTls = true,
                        Username = "operator",
                        Password = " secret with spaces "
                    }
                },
                Profiles =
                [
                    new SupervisorProfileConfig
                    {
                        Name = "MQTT",
                        MonitorProcess = "mqtt-trigger.exe",
                        MqttResources =
                        [
                            new MqttResourceConfig
                            {
                                Topic = "devices/light/set",
                                Payload = "{\"on\":true}",
                                Qos = MqttQualityOfService.ExactlyOnce,
                                Retain = true,
                                VerifyStateChange = true,
                                VerificationTopic = "devices/light/state",
                                ExpectedState = "ON",
                                VerificationTimeoutSeconds = 9,
                                DeactivationBehavior =
                                    MqttDeactivationBehavior.PublishConfiguredPayload,
                                DeactivationTopic = "devices/light/set",
                                DeactivationPayload = "{\"on\":false}",
                                DeactivationQos = MqttQualityOfService.AtMostOnce,
                                DeactivationRetain = true,
                                VerifyDeactivation = true,
                                DeactivationExpectedState = "OFF",
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
            MqttResourceConfig resource = Assert.Single(
                Assert.Single(loaded.Profiles).MqttResources
            );

            Assert.Equal("broker.example.test", loaded.Integrations.Mqtt.Host);
            Assert.Equal(8883, loaded.Integrations.Mqtt.Port);
            Assert.True(loaded.Integrations.Mqtt.UseTls);
            Assert.Equal("operator", loaded.Integrations.Mqtt.Username);
            Assert.Equal(" secret with spaces ", loaded.Integrations.Mqtt.Password);
            Assert.Equal("devices/light/set", resource.Topic);
            Assert.Equal("{\"on\":true}", resource.Payload);
            Assert.Equal(MqttQualityOfService.ExactlyOnce, resource.Qos);
            Assert.Equal(MqttDeactivationBehavior.PublishConfiguredPayload,
                resource.DeactivationBehavior);
            Assert.Equal("{\"on\":false}", resource.DeactivationPayload);
            Assert.Equal([NotificationTarget.Windows], resource.Notifications.Target);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("mqtt://broker.example", "without a URI scheme")]
    [InlineData("broker.example/path", "without a URI scheme")]
    public void Validate_InvalidBrokerHost_ThrowsClearError(string host, string expected)
    {
        var integrations = new IntegrationsConfig
        {
            Mqtt = new MqttIntegrationConfig { Host = host }
        };

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => IntegrationConfigValidator.Validate(integrations)
        );

        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_PasswordWithoutUsername_ThrowsClearError()
    {
        var integrations = new IntegrationsConfig
        {
            Mqtt = new MqttIntegrationConfig
            {
                Host = "broker.example",
                Password = "secret"
            }
        };

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => IntegrationConfigValidator.Validate(integrations)
        );

        Assert.Contains("username", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RetainedRestoreRequiresRetainedInverse()
    {
        SupervisorProfileConfig profile = CreateProfile(new MqttResourceConfig
        {
            Topic = "device/set",
            VerificationTopic = "device/state",
            DeactivationBehavior = MqttDeactivationBehavior.RestoreRetainedState,
            DeactivationTopic = "device/set",
            DeactivationRetain = false
        });

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => ConfigValidator.Validate([profile])
        );

        Assert.Contains("deactivationRetain must be true", exception.Message);
    }

    [Fact]
    public void Validate_ExactTopicsRejectWildcards()
    {
        SupervisorProfileConfig profile = CreateProfile(new MqttResourceConfig
        {
            Topic = "device/+/set"
        });

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => ConfigValidator.Validate([profile])
        );

        Assert.Contains("without nulls or wildcards", exception.Message);
    }

    [Fact]
    public void Validate_OneShotPublish_AllowsNoInverse()
    {
        SupervisorProfileConfig profile = CreateProfile(new MqttResourceConfig
        {
            Topic = "events/profile-active",
            Payload = "started",
            DeactivationBehavior = MqttDeactivationBehavior.OneShot
        });

        ConfigValidator.Validate([profile]);
    }

    private static SupervisorProfileConfig CreateProfile(MqttResourceConfig resource) => new()
    {
        Name = "MQTT",
        MonitorProcess = "mqtt-trigger.exe",
        MqttResources = [resource]
    };

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.MqttTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(path);
        return path;
    }
}
