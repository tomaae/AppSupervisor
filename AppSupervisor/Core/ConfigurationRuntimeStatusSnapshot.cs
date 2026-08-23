using AppSupervisor.Configuration;
using AppSupervisor.Resources;

namespace AppSupervisor.Core;

/// <summary>Describes one cached application or service lifecycle state for configuration UI display.</summary>
internal enum ConfigurationResourceRuntimeStatus
{
    Unknown,
    Starting,
    Running,
    Stopping,
    NotRunning
}

/// <summary>Identifies one configured resource within its owning profile.</summary>
internal readonly record struct ConfigurationResourceRuntimeStatusKey(
    string ProfileId,
    string ResourceId);

/// <summary>Provides one immutable, query-free view of cached application and service states.</summary>
internal sealed record ConfigurationRuntimeStatusSnapshot(
    IReadOnlyDictionary<ConfigurationResourceRuntimeStatusKey, ConfigurationResourceRuntimeStatus>
        Resources)
{
    internal static ConfigurationRuntimeStatusSnapshot Empty { get; } = new(
        new Dictionary<ConfigurationResourceRuntimeStatusKey, ConfigurationResourceRuntimeStatus>()
    );

    internal ConfigurationResourceRuntimeStatus GetStatus(string profileId, string resourceId) =>
        Resources.TryGetValue(
            new ConfigurationResourceRuntimeStatusKey(profileId, resourceId),
            out ConfigurationResourceRuntimeStatus status)
                ? status
                : ConfigurationResourceRuntimeStatus.Unknown;

    /// <summary>Compares status values rather than snapshot or dictionary identities.</summary>
    internal bool HasSameStatuses(ConfigurationRuntimeStatusSnapshot other)
    {
        if (ReferenceEquals(this, other))
            return true;

        if (Resources.Count != other.Resources.Count)
            return false;

        foreach ((ConfigurationResourceRuntimeStatusKey key,
            ConfigurationResourceRuntimeStatus status) in Resources)
        {
            if (!other.Resources.TryGetValue(key, out ConfigurationResourceRuntimeStatus otherStatus) ||
                status != otherStatus)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Builds UI status snapshots exclusively from runtime-owned cached fields.</summary>
internal static class ConfigurationRuntimeStatusSnapshotFactory
{
    internal static ConfigurationRuntimeStatusSnapshot Create(
        AppSupervisorConfig configuration,
        IReadOnlyList<SupervisorProfile> runtimeProfiles)
    {
        var runtimeByName = runtimeProfiles.ToDictionary(
            profile => profile.Name,
            StringComparer.OrdinalIgnoreCase
        );
        var statuses = new Dictionary<
            ConfigurationResourceRuntimeStatusKey,
            ConfigurationResourceRuntimeStatus>();

        foreach (SupervisorProfileConfig profileConfig in configuration.Profiles)
        {
            runtimeByName.TryGetValue(profileConfig.Name, out SupervisorProfile? runtimeProfile);

            foreach (ManagedApplicationConfig applicationConfig in profileConfig.Applications)
            {
                IManagedResource? resource = runtimeProfile?.FindResource(
                    applicationConfig.ResourceId
                );
                ManagedApplication? application = resource as ManagedApplication ??
                    (resource as HealthCheckedApplication)?.ApiApplication as ManagedApplication;
                AddStatus(
                    statuses,
                    profileConfig.ProfileId,
                    applicationConfig.ResourceId,
                    application?.CachedRuntimeStatus ??
                        ConfigurationResourceRuntimeStatus.Unknown
                );
            }

            foreach (ManagedServiceConfig serviceConfig in profileConfig.Services)
            {
                ManagedService? service = runtimeProfile?.FindResource(serviceConfig.ResourceId)
                    as ManagedService;
                AddStatus(
                    statuses,
                    profileConfig.ProfileId,
                    serviceConfig.ResourceId,
                    service?.CachedRuntimeStatus ?? ConfigurationResourceRuntimeStatus.Unknown
                );
            }
        }

        return new ConfigurationRuntimeStatusSnapshot(statuses);
    }

    private static void AddStatus(
        IDictionary<ConfigurationResourceRuntimeStatusKey, ConfigurationResourceRuntimeStatus>
            statuses,
        string profileId,
        string resourceId,
        ConfigurationResourceRuntimeStatus status)
    {
        if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(resourceId))
            return;

        statuses[new ConfigurationResourceRuntimeStatusKey(profileId, resourceId)] = status;
    }
}
