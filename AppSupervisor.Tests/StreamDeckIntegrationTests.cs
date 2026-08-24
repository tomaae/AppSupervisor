using AppSupervisor.Configuration;

namespace AppSupervisor.Tests;

/// <summary>Verifies Stream Deck action configuration persistence and validation.</summary>
public sealed class StreamDeckIntegrationTests
{
    [Fact]
    public void SerializeAndLoad_Action_RoundTripsAndNormalizesNames()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.StreamDeckTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "config.json");

        try
        {
            ConfigFileWriter.SaveAtomic(path, new AppSupervisorConfig
            {
                Profiles =
                [
                    new SupervisorProfileConfig
                    {
                        Name = "Stream Deck",
                        MonitorProcess = "vr.exe",
                        StreamDeckResources =
                        [
                            new StreamDeckResourceConfig
                            {
                                ActionId = " 4979ce49-d88b-49cb-9a80-1e95eb45d8f9 ",
                                ActionName = " Start VR ",
                                ActionTitle = " VR ",
                                IsSwitch = true,
                                RestoreSwitchOnDeactivate = true
                            }
                        ]
                    }
                ]
            });

            StreamDeckResourceConfig action = Assert.Single(
                Assert.Single(ConfigLoader.Load(path).Profiles).StreamDeckResources
            );
            Assert.Equal("4979ce49-d88b-49cb-9a80-1e95eb45d8f9", action.ActionId);
            Assert.Equal("Start VR", action.ActionName);
            Assert.Equal("VR", action.ActionTitle);
            Assert.True(action.IsSwitch);
            Assert.True(action.RestoreSwitchOnDeactivate);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Validate_ButtonConfiguredForRestoration_ThrowsValidationError()
    {
        var profile = new SupervisorProfileConfig
        {
            Name = "Stream Deck",
            MonitorProcess = "vr.exe",
            StreamDeckResources =
            [
                new StreamDeckResourceConfig
                {
                    ActionId = "action-1",
                    RestoreSwitchOnDeactivate = true
                }
            ]
        };

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => ConfigValidator.Validate([profile])
        );

        Assert.Contains("only when the selected action is a switch", exception.Message);
    }

    [Fact]
    public void Validate_EnabledActionWithoutTool_ThrowsValidationError()
    {
        var profile = new SupervisorProfileConfig
        {
            Name = "Stream Deck",
            MonitorProcess = "vr.exe",
            StreamDeckResources = [new StreamDeckResourceConfig()]
        };

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => ConfigValidator.Validate([profile])
        );

        Assert.Contains("must have a selected action", exception.Message);
    }
}
