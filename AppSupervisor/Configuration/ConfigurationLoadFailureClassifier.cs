using System.Text.Json;
using AppSupervisor.Discovery;

namespace AppSupervisor.Configuration;

/// <summary>Contains user-facing presentation for one configuration replacement failure.</summary>
/// <param name="TrayStatus">The concise tray tooltip status.</param>
/// <param name="NotificationTitle">The notification heading.</param>
/// <param name="MessagePrefix">The state and recovery consequence shown before exception details.</param>
/// <param name="LogMessage">The diagnostic log context.</param>
internal readonly record struct ConfigurationLoadFailurePresentation(
    string TrayStatus,
    string NotificationTitle,
    string MessagePrefix,
    string LogMessage
);

/// <summary>Separates invalid configuration from discovery and runtime-construction failures.</summary>
internal static class ConfigurationLoadFailureClassifier
{
    /// <summary>Creates accurate tray, notification, and log text for one failed load.</summary>
    /// <param name="exception">The load, validation, discovery, or construction failure.</param>
    /// <param name="hasValidConfiguration">Whether an earlier accepted configuration remains active.</param>
    public static ConfigurationLoadFailurePresentation Classify(
        Exception exception,
        bool hasValidConfiguration)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is ApplicationDiscoveryException)
        {
            return new ConfigurationLoadFailurePresentation(
                "Application discovery error",
                "Application discovery error",
                hasValidConfiguration
                    ? "Reload could not refresh installed applications. Existing configuration remains active."
                    : "Installed applications could not be discovered. Supervision is paused.",
                "Installed-application discovery prevented configuration loading."
            );
        }

        if (exception is ConfigValidationException or
            JsonException or
            IOException or
            UnauthorizedAccessException)
        {
            return new ConfigurationLoadFailurePresentation(
                "Configuration error",
                "Configuration error",
                hasValidConfiguration
                    ? "Reload failed. Existing configuration remains active."
                    : "Configuration is invalid or unavailable. Supervision is paused.",
                "Configuration loading or validation failed."
            );
        }

        return new ConfigurationLoadFailurePresentation(
            "Startup error",
            "Startup error",
            hasValidConfiguration
                ? "Reload could not construct the replacement runtime. Existing configuration remains active."
                : "The supervision runtime could not be constructed. Supervision is paused.",
            "Runtime construction prevented configuration loading."
        );
    }
}
