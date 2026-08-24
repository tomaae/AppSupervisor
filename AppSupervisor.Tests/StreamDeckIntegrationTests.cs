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
                                ToolName = " streamdeck__start_vr ",
                                ActionName = " Start VR "
                            }
                        ]
                    }
                ]
            });

            StreamDeckResourceConfig action = Assert.Single(
                Assert.Single(ConfigLoader.Load(path).Profiles).StreamDeckResources
            );
            Assert.Equal("streamdeck__start_vr", action.ToolName);
            Assert.Equal("Start VR", action.ActionName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
