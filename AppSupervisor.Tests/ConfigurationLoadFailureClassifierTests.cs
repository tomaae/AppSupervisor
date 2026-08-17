using System.Text.Json;
using AppSupervisor.Configuration;
using AppSupervisor.Discovery;

namespace AppSupervisor.Tests;

/// <summary>Verifies load failures are presented according to their actual source.</summary>
public sealed class ConfigurationLoadFailureClassifierTests
{
    /// <summary>Confirms exhausted installed-app discovery is not described as invalid configuration.</summary>
    [Fact]
    public void Classify_ApplicationDiscoveryFailure_UsesDiscoveryError()
    {
        var exception = new ApplicationDiscoveryException(
            "Windows Store",
            4,
            new TimeoutException("Windows Store application discovery timed out.")
        );

        ConfigurationLoadFailurePresentation presentation =
            ConfigurationLoadFailureClassifier.Classify(
                exception,
                hasValidConfiguration: false
            );

        Assert.Equal("Application discovery error", presentation.TrayStatus);
        Assert.Equal("Application discovery error", presentation.NotificationTitle);
        Assert.Contains("Supervision is paused", presentation.MessagePrefix);
        Assert.DoesNotContain("invalid", presentation.MessagePrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Confirms a failed discovery reload explains that accepted supervision remains active.</summary>
    [Fact]
    public void Classify_DiscoveryReloadFailure_PreservesExistingConfigurationMessage()
    {
        var exception = new ApplicationDiscoveryException(
            "Steam",
            4,
            new IOException("Library unavailable.")
        );

        ConfigurationLoadFailurePresentation presentation =
            ConfigurationLoadFailureClassifier.Classify(
                exception,
                hasValidConfiguration: true
            );

        Assert.Contains("Existing configuration remains active", presentation.MessagePrefix);
    }

    /// <summary>Confirms malformed JSON remains a genuine configuration error.</summary>
    [Fact]
    public void Classify_JsonFailure_UsesConfigurationError()
    {
        ConfigurationLoadFailurePresentation presentation =
            ConfigurationLoadFailureClassifier.Classify(
                new JsonException("Malformed JSON."),
                hasValidConfiguration: false
            );

        Assert.Equal("Configuration error", presentation.TrayStatus);
        Assert.Equal("Configuration error", presentation.NotificationTitle);
    }

    /// <summary>Confirms runtime graph failures are not mislabeled as configuration validation errors.</summary>
    [Fact]
    public void Classify_RuntimeConstructionFailure_UsesStartupError()
    {
        ConfigurationLoadFailurePresentation presentation =
            ConfigurationLoadFailureClassifier.Classify(
                new InvalidOperationException("Runtime could not be constructed."),
                hasValidConfiguration: false
            );

        Assert.Equal("Startup error", presentation.TrayStatus);
        Assert.Equal("Startup error", presentation.NotificationTitle);
    }
}
