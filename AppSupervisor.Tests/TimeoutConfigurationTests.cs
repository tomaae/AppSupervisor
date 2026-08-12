using AppSupervisor.Configuration;

namespace AppSupervisor.Tests;

/// <summary>Verifies the canonical close and restart timeout configuration contract.</summary>
public sealed class TimeoutConfigurationTests
{
    /// <summary>Confirms saved configuration uses the canonical timeout property names.</summary>
    [Fact]
    public void Serialize_Timeouts_WritesCanonicalNames()
    {
        var profile = new SupervisorProfileConfig
        {
            Name = "Canonical",
            Enabled = false,
            MonitorProcess = "Canonical.exe",
            WaitBeforeStartingResourcesMilliseconds = 250,
            CloseTimeoutSeconds = 12,
            RestartTimeoutSeconds = 34
        };

        string json = ConfigFileWriter.Serialize(new AppSupervisorConfig { Profiles = [profile] });

        Assert.Contains("\"closeTimeoutSeconds\": 12", json, StringComparison.Ordinal);
        Assert.Contains("\"restartTimeoutSeconds\": 34", json, StringComparison.Ordinal);
        Assert.Contains(
            "\"waitBeforeStartingResourcesMilliseconds\": 250",
            json,
            StringComparison.Ordinal
        );
    }
}
