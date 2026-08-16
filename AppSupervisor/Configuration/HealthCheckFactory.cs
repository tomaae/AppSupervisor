using AppSupervisor.Health;

namespace AppSupervisor.Configuration;

/// <summary>
/// Converts validated health-check configuration into production state machines and reusable one-shot test components.
/// </summary>
public static class HealthCheckFactory
{
    internal const string ResponsivenessCheckName = "Application responsiveness";

    /// <summary>Creates one runtime health check with the correct probe and automatic activation condition.</summary>
    /// <param name="config">The validated health-check configuration.</param>
    /// <returns>A fresh health-check runtime instance.</returns>
    public static ManagedHealthCheck Create(HealthCheckConfig config)
    {
        return new ManagedHealthCheck(
            config,
            CreateProbe(config),
            CreateActivationCondition(config)
        );
    }

    /// <summary>Creates the fixed, helper-level responsiveness check used by the Applications checkbox.</summary>
    /// <param name="application">The helper whose notification targets are reused by responsiveness recovery.</param>
    /// <returns>A conservative, independently debounced responsiveness check.</returns>
    internal static ManagedHealthCheck CreateApplicationResponsiveness(
        ManagedApplicationConfig application)
    {
        var config = new HealthCheckConfig
        {
            Enabled = true,
            Name = ResponsivenessCheckName,
            IntervalSeconds = 10,
            TimeoutSeconds = 3,
            FailureThreshold = 3,
            StartupDelaySeconds = 20,
            RestartOnFailure = true,
            Notifications = new NotificationConfig
            {
                Target = [.. application.Notifications.Target]
            }
        };

        return new ManagedHealthCheck(
            config,
            new ApplicationResponsivenessProbe(),
            new AlwaysActiveCondition()
        );
    }

    /// <summary>Creates the raw probe used by production monitoring and UI one-shot tests.</summary>
    /// <param name="config">The validated health-check configuration.</param>
    /// <returns>A fresh listener or VRChat OSCQuery probe.</returns>
    internal static IHealthProbe CreateProbe(HealthCheckConfig config)
    {
        return config.Type switch
        {
            HealthCheckType.Listener => new ListenerHealthProbe(
                config.Protocol!.Value,
                config.Port!.Value
            ),
            HealthCheckType.Vrcosc => new VrcOscQueryProbe(
                config.Parameters,
                config.StaleSeconds is int staleSeconds
                    ? TimeSpan.FromSeconds(staleSeconds)
                    : null
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported health-check type: {config.Type}."
            )
        };
    }

    /// <summary>Creates the prerequisite condition used by production monitoring and UI one-shot tests.</summary>
    /// <param name="config">The validated health-check configuration.</param>
    /// <returns>An always-active or process-running condition.</returns>
    internal static IHealthCheckActivationCondition CreateActivationCondition(
        HealthCheckConfig config)
    {
        return config.Type switch
        {
            HealthCheckType.Listener => string.IsNullOrWhiteSpace(config.ActiveWhenProcess)
                ? new AlwaysActiveCondition()
                : new ProcessRunningCondition(config.ActiveWhenProcess),
            HealthCheckType.Vrcosc => new ProcessRunningCondition("VRChat.exe"),
            _ => throw new InvalidOperationException(
                $"Unsupported health-check type: {config.Type}."
            )
        };
    }
}
