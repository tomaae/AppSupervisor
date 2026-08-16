using AppSupervisor.Configuration;
using AppSupervisor.Notifications;

namespace AppSupervisor.Tests;

/// <summary>Verifies Twitch configuration persistence and cross-profile safety.</summary>
public sealed class TwitchIntegrationTests
{
    [Fact]
    public void SerializeAndLoad_TwitchActions_RoundTripsActions()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "config.json");
        try
        {
            ConfigFileWriter.SaveAtomic(path, new AppSupervisorConfig
            {
                Integrations = new IntegrationsConfig
                {
                    Twitch = new TwitchIntegrationConfig()
                },
                Profiles =
                [
                    new SupervisorProfileConfig
                    {
                        Name = "Stream",
                        MonitorProcess = "obs64.exe",
                        TwitchResources =
                        [
                            new TwitchResourceConfig
                            {
                                Action = TwitchActionType.SendChatMessage,
                                Message = " Hello twitchdevHype ",
                                Notifications = new NotificationConfig
                                {
                                    Target = [NotificationTarget.Windows]
                                }
                            },
                            new TwitchResourceConfig
                            {
                                Action = TwitchActionType.RunCommercial,
                                CommercialLengthSeconds = 180
                            }
                        ]
                    }
                ]
            });

            AppSupervisorConfig loaded = ConfigLoader.Load(path);
            Assert.Equal(2, loaded.Profiles[0].TwitchResources.Count);
            Assert.Equal("Hello twitchdevHype", loaded.Profiles[0].TwitchResources[0].Message);
            Assert.Equal(180, loaded.Profiles[0].TwitchResources[1].CommercialLengthSeconds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Validate_TwitchInTwoEnabledProfiles_IsRejected()
    {
        SupervisorProfileConfig Create(string name) => new()
        {
            Name = name,
            MonitorProcess = $"{name}.exe",
            TwitchResources =
            [
                new TwitchResourceConfig
                {
                    Action = TwitchActionType.EmoteOnly
                }
            ]
        };

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => ConfigValidator.Validate([Create("First"), Create("Second")])
        );

        Assert.Contains("Only one enabled profile", exception.Message);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(120)]
    [InlineData(150)]
    [InlineData(180)]
    public void Validate_AllSupportedCommercialLengths_AreAccepted(int length)
    {
        var profile = new SupervisorProfileConfig
        {
            Name = "Ad",
            MonitorProcess = "obs64.exe",
            TwitchResources =
            [
                new TwitchResourceConfig
                {
                    Action = TwitchActionType.RunCommercial,
                    CommercialLengthSeconds = length
                }
            ]
        };

        ConfigValidator.Validate([profile]);
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"AppSupervisor.TwitchTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
