using AppSupervisor.Configuration;

namespace AppSupervisor.Tests;

/// <summary>
/// Verifies optional Steam app URIs remain separate from executable-path process identity.
/// </summary>
public sealed class SteamLaunchConfigTests
{
    /// <summary>Accepts a positive Steam rungameid URI while retaining an existing executable identity.</summary>
    [Fact]
    public void Validate_SteamRunGameIdWithoutArguments_Succeeds()
    {
        SupervisorProfileConfig profile = CreateProfile("steam://rungameid/1173510", "");

        ConfigValidator.Validate([profile]);

        Assert.Equal(
            "steam://rungameid/1173510",
            profile.Applications[0].AppUri
        );
        Assert.Equal(Environment.ProcessPath, profile.Applications[0].Path);
    }

    /// <summary>Rejects arbitrary shell protocols so appUri cannot become an unrestricted command target.</summary>
    [Fact]
    public void Validate_NonSteamAppUri_ThrowsValidationError()
    {
        SupervisorProfileConfig profile = CreateProfile("https://example.com", "");

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => ConfigValidator.Validate([profile])
        );

        Assert.Contains("steam://rungameid", exception.Message);
    }

    /// <summary>Rejects direct executable arguments that Steam protocol launches cannot reliably forward.</summary>
    [Fact]
    public void Validate_SteamAppUriWithArguments_ThrowsValidationError()
    {
        SupervisorProfileConfig profile = CreateProfile(
            "steam://rungameid/1173510",
            "--unsupported"
        );

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => ConfigValidator.Validate([profile])
        );

        Assert.Contains("cannot configure arguments", exception.Message);
    }

    /// <summary>Creates an otherwise valid enabled helper configuration for launch validation.</summary>
    /// <param name="appUri">The optional app URI.</param>
    /// <param name="arguments">The direct-launch arguments.</param>
    /// <returns>A valid supervisor profile except for the requested launch combination.</returns>
    private static SupervisorProfileConfig CreateProfile(string appUri, string arguments)
    {
        return new SupervisorProfileConfig
        {
            Name = "Steam launch test",
            MonitorProcess = "notepad.exe",
            Applications =
            [
                new ManagedApplicationConfig
                {
                    Path = Environment.ProcessPath!,
                    AppUri = appUri,
                    Arguments = arguments,
                    Notifications = new NotificationConfig { Target = [] },
                    HealthChecks = []
                }
            ],
            Services = []
        };
    }
}
