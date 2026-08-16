using AppSupervisor.Configuration;

namespace AppSupervisor.Tests;

/// <summary>Verifies Windows audio endpoint configuration persistence and validation.</summary>
public sealed class AudioInterfaceConfigTests
{
    [Fact]
    public void SaveAndLoad_AudioInterface_RoundTripsIdentityStateAndRestoreChoice()
    {
        string directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.AudioConfigTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directoryPath);
        string configPath = Path.Combine(directoryPath, "config.json");

        try
        {
            ConfigFileWriter.SaveAtomic(configPath, new AppSupervisorConfig
            {
                Profiles =
                [
                    new SupervisorProfileConfig
                    {
                        Name = "Audio",
                        MonitorProcess = "game.exe",
                        AudioInterfaces =
                        [
                            new AudioInterfaceResourceConfig
                            {
                                EndpointId = "endpoint-id",
                                DeviceInstanceId = "device-instance",
                                ContainerId = "0e1c02c3-5bb7-40f8-9f7f-18372514906d",
                                FriendlyName = "Desk speakers",
                                InterfaceName = "USB Audio",
                                Direction = AudioInterfaceDirection.Output,
                                UseDefaultDevice = true,
                                VolumePercent = 63,
                                Muted = true,
                                RestoreOnDeactivate = false
                            }
                        ]
                    }
                ]
            });

            AppSupervisorConfig loaded = ConfigLoader.Load(configPath);
            AudioInterfaceResourceConfig audio = Assert.Single(
                Assert.Single(loaded.Profiles).AudioInterfaces
            );

            Assert.Equal("endpoint-id", audio.EndpointId);
            Assert.Equal("device-instance", audio.DeviceInstanceId);
            Assert.Equal("0e1c02c3-5bb7-40f8-9f7f-18372514906d", audio.ContainerId);
            Assert.Equal("Desk speakers", audio.FriendlyName);
            Assert.Equal("USB Audio", audio.InterfaceName);
            Assert.Equal(AudioInterfaceDirection.Output, audio.Direction);
            Assert.True(audio.UseDefaultDevice);
            Assert.Equal(63, audio.VolumePercent);
            Assert.True(audio.Muted);
            Assert.False(audio.RestoreOnDeactivate);
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public void Validate_AudioVolumeOutsidePercentageRange_RejectsConfiguration()
    {
        var profile = new SupervisorProfileConfig
        {
            Name = "Invalid audio",
            MonitorProcess = "game.exe",
            AudioInterfaces =
            [
                new AudioInterfaceResourceConfig
                {
                    EndpointId = "endpoint",
                    FriendlyName = "Speakers",
                    VolumePercent = 101
                }
            ]
        };

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(() =>
            ConfigValidator.Validate([profile])
        );

        Assert.Contains("volumePercent", exception.Message, StringComparison.Ordinal);
    }
}
