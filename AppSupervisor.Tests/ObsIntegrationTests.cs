using System.Security.Cryptography;
using System.Text;
using AppSupervisor.Configuration;
using AppSupervisor.Obs;

namespace AppSupervisor.Tests;

/// <summary>Verifies OBS configuration and protocol primitives.</summary>
public sealed class ObsIntegrationTests
{
    [Fact]
    public void SerializeAndLoad_ObsAction_RoundTripsConnectionAndOneWayAction()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "config.json");

        try
        {
            ConfigFileWriter.SaveAtomic(path, new AppSupervisorConfig
            {
                Integrations = new IntegrationsConfig
                {
                    Obs = new ObsIntegrationConfig
                    {
                        Host = " 192.0.2.1 ",
                        Port = 4455,
                        Password = " password with spaces "
                    }
                },
                Profiles =
                [
                    new SupervisorProfileConfig
                    {
                        Name = "OBS",
                        MonitorProcess = "obs64.exe",
                        ObsResources =
                        [
                            new ObsResourceConfig
                            {
                                Action = ObsActionType.SetSourceVisibility,
                                SceneName = " Scene ",
                                SourceName = " Camera ",
                                Visible = false
                            }
                        ]
                    }
                ]
            });

            AppSupervisorConfig loaded = ConfigLoader.Load(path);
            ObsResourceConfig action = Assert.Single(Assert.Single(loaded.Profiles).ObsResources);

            Assert.Equal("192.0.2.1", loaded.Integrations.Obs.Host);
            Assert.Equal(4455, loaded.Integrations.Obs.Port);
            Assert.Equal(" password with spaces ", loaded.Integrations.Obs.Password);
            Assert.Equal(ObsActionType.SetSourceVisibility, action.Action);
            Assert.Equal("Scene", action.SceneName);
            Assert.Equal("Camera", action.SourceName);
            Assert.False(action.Visible);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Validate_EnabledMuteActionWithoutInput_ThrowsValidationError()
    {
        var profile = new SupervisorProfileConfig
        {
            Name = "OBS",
            MonitorProcess = "obs64.exe",
            ObsResources =
            [
                new ObsResourceConfig { Action = ObsActionType.SetInputMute }
            ]
        };

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => ConfigValidator.Validate([profile])
        );

        Assert.Contains("inputName", exception.Message);
    }

    [Fact]
    public void AuthenticationResponse_UsesObsWebSocketV5ChallengeFormula()
    {
        const string password = "secret";
        const string salt = "salt-value";
        const string challenge = "challenge-value";
        string firstHash = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(password + salt))
        );
        string expected = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(firstHash + challenge))
        );

        string actual = ObsWebSocketClient.CreateAuthenticationResponse(
            password,
            salt,
            challenge
        );

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CreateEndpoint_UsesStandardWsScheme()
    {
        Uri endpoint = ObsWebSocketClient.CreateEndpoint(new ObsIntegrationConfig
        {
            Host = "192.0.2.30",
            Port = 4455
        });

        Assert.Equal("ws", endpoint.Scheme);
        Assert.Equal("192.0.2.30", endpoint.Host);
        Assert.Equal(4455, endpoint.Port);
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.ObsTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(path);
        return path;
    }
}
