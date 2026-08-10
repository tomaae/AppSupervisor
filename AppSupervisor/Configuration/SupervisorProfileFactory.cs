using AppSupervisor.Core;
using AppSupervisor.Resources;
using AppSupervisor.Triggers;

namespace AppSupervisor.Configuration;

/// <summary>
/// Converts configuration models into the runtime trigger and managed-resource graph.
/// </summary>
public static class SupervisorProfileFactory
{
    private const int DefaultCloseTimeoutSeconds = 20;
    private const int DefaultRestartTimeoutSeconds = 20;

    /// <summary>
    /// Builds a process-triggered supervisor profile from one validated configuration entry.
    /// </summary>
    /// <param name="config">The validated configuration entry to translate.</param>
    /// <returns>A fresh supervisor profile containing configured applications and Windows services.</returns>
    public static SupervisorProfile Create(SupervisorProfileConfig config)
    {
        var trigger = new ProcessTrigger(config.MonitorProcess);

        int restartTimeoutSeconds =
            config.RestartTimeoutSeconds ?? DefaultRestartTimeoutSeconds;
        var restartTimeout = TimeSpan.FromSeconds(restartTimeoutSeconds);

        var resources = new List<IManagedResource>();

        foreach (ManagedApplicationConfig applicationConfig in
            config.Applications.Where(application => application.Enabled))
        {
            var application = new ManagedApplication(
                applicationConfig,
                restartTimeout
            );
            var healthChecks = applicationConfig.HealthChecks
                .Where(healthCheck => healthCheck.Enabled)
                .Select(HealthCheckFactory.Create)
                .ToList();

            if (applicationConfig.MonitorResponsiveness)
            {
                healthChecks.Insert(
                    0,
                    HealthCheckFactory.CreateApplicationResponsiveness(applicationConfig)
                );
            }

            resources.Add(healthChecks.Count == 0
                ? application
                : new HealthCheckedApplication(application, healthChecks));
        }

        resources.AddRange(config.Services
            .Where(service => service.Enabled)
            .Select(service => (IManagedResource)new ManagedService(
                service,
                restartTimeout
            )));

        int closeTimeoutSeconds =
            config.CloseTimeoutSeconds ?? DefaultCloseTimeoutSeconds;

        return new SupervisorProfile(
            config.Name,
            config.MonitorProcess,
            trigger,
            resources,
            TimeSpan.FromSeconds(closeTimeoutSeconds)
        );
    }
}
