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
        => Create(config, _ => null);

    /// <summary>
    /// Builds a profile whose applications consult global shared-helper ownership before closing.
    /// </summary>
    /// <param name="config">The validated configuration entry to translate.</param>
    /// <param name="closeGuardFactory">Creates the close guard for each enabled application entry.</param>
    /// <returns>A fresh supervisor profile containing configured applications and Windows services.</returns>
    internal static SupervisorProfile Create(
        SupervisorProfileConfig config,
        Func<ManagedApplicationConfig, Func<bool>?> closeGuardFactory)
    {
        var trigger = new ProcessTrigger(config.MonitorProcess);

        int restartTimeoutSeconds =
            config.RestartTimeoutSeconds ?? DefaultRestartTimeoutSeconds;
        var restartTimeout = TimeSpan.FromSeconds(restartTimeoutSeconds);

        var configuredResources = new List<(
            ManagedResourceConfig Config,
            IManagedResource Resource,
            int StableOrder)>();
        int stableOrder = 0;

        foreach (ManagedApplicationConfig applicationConfig in
            config.Applications.Where(application => application.Enabled))
        {
            var application = new ManagedApplication(
                applicationConfig,
                restartTimeout,
                closeGuardFactory(applicationConfig)
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

            IManagedResource resource = healthChecks.Count == 0
                ? application
                : new HealthCheckedApplication(application, healthChecks);
            configuredResources.Add((applicationConfig, resource, stableOrder++));
        }

        foreach (ManagedServiceConfig serviceConfig in
            config.Services.Where(service => service.Enabled))
        {
            configuredResources.Add((
                serviceConfig,
                new ManagedService(serviceConfig, restartTimeout),
                stableOrder++
            ));
        }

        ManagedResourceStartup[] startupResources = configuredResources
            .OrderBy(item => item.Config.StartupOrder < 0
                ? int.MaxValue
                : item.Config.StartupOrder)
            .ThenBy(item => item.StableOrder)
            .Select(item => new ManagedResourceStartup(
                item.Resource,
                item.Config.ResourceId,
                item.Config.WaitAfterStartupMilliseconds,
                item.Config.DependencyResourceId
            ))
            .ToArray();

        int closeTimeoutSeconds =
            config.CloseTimeoutSeconds ?? DefaultCloseTimeoutSeconds;

        return new SupervisorProfile(
            config.Name,
            config.MonitorProcess,
            trigger,
            startupResources,
            TimeSpan.FromSeconds(closeTimeoutSeconds)
        );
    }
}
