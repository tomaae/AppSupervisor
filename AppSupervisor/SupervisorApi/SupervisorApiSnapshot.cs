using AppSupervisor.Core;
using AppSupervisor.Configuration;
using AppSupervisor.Health;
using AppSupervisor.Resources;

namespace AppSupervisor.SupervisorApi;

/// <summary>Immutable state published by the one-second supervision timer for API readers.</summary>
internal sealed record SupervisorApiSnapshot(
    DateTime UpdatedUtc,
    bool Paused,
    IReadOnlyList<SupervisorApiProfileSnapshot> Profiles)
{
    public static SupervisorApiSnapshot Empty { get; } =
        new(DateTime.UnixEpoch, Paused: true, []);
}

internal sealed record SupervisorApiProfileSnapshot(
    string Name,
    string InternalId,
    bool Enabled,
    string Status,
    string TriggerType,
    string MonitorProcess,
    string MonitorBluetoothDeviceId,
    IReadOnlyList<SupervisorApiHelperSnapshot> Helpers);

internal sealed record SupervisorApiHelperSnapshot(
    string Name,
    string InternalId,
    bool Enabled,
    bool Active,
    string Status,
    string Path,
    string AppUri,
    string Arguments,
    bool Restart,
    bool EnsureClosedUntilNeeded,
    bool LeaveRunningAfterProfileStops,
    bool MinimizeAfterStart,
    bool MonitorResponsiveness,
    IReadOnlyList<SupervisorApiHealthCheckSnapshot> HealthChecks,
    SupervisorApiMacroSnapshot Macro);

internal sealed record SupervisorApiHealthCheckSnapshot(
    string Name,
    bool Enabled,
    bool Active,
    string Status,
    string Detail,
    HealthCheckType? Type,
    ListenerProtocol? Protocol,
    int? Port,
    string ActiveWhenProcess,
    int IntervalSeconds,
    int TimeoutSeconds,
    int FailureThreshold,
    int StartupDelaySeconds,
    bool RestartOnFailure,
    IReadOnlyList<string> Parameters,
    int? StaleSeconds);

internal sealed record SupervisorApiMacroSnapshot(
    bool Configured,
    string Status,
    IReadOnlyList<StartupMacroActionConfig> Actions);

/// <summary>Builds API documents exclusively from configuration and runtime-owned cached state.</summary>
internal static class SupervisorApiSnapshotFactory
{
    public static SupervisorApiSnapshot Create(
        AppSupervisorConfig configuration,
        IReadOnlyList<SupervisorProfile> runtimeProfiles,
        bool paused)
    {
        var runtimeByName = runtimeProfiles.ToDictionary(
            profile => profile.Name,
            StringComparer.OrdinalIgnoreCase
        );
        var profiles = new List<SupervisorApiProfileSnapshot>(configuration.Profiles.Count);

        foreach (SupervisorProfileConfig profileConfig in configuration.Profiles)
        {
            runtimeByName.TryGetValue(profileConfig.Name, out SupervisorProfile? runtimeProfile);
            string profileStatus = !profileConfig.Enabled
                ? "disabled"
                : paused
                    ? "paused"
                    : runtimeProfile?.TriggerActive == true
                        ? "active"
                        : "inactive";
            var helpers = new List<SupervisorApiHelperSnapshot>(profileConfig.Applications.Count);

            foreach (ManagedApplicationConfig helperConfig in profileConfig.Applications)
            {
                IManagedResource? resource = runtimeProfile?.FindResource(helperConfig.ResourceId);
                bool active = helperConfig.Enabled &&
                    runtimeProfile is not null && resource is not null &&
                    runtimeProfile.IsResourceActivated(resource);
                HealthCheckedApplication? healthWrapper = resource as HealthCheckedApplication;
                ManagedApplication? application = resource as ManagedApplication ??
                    healthWrapper?.ApiApplication as ManagedApplication;
                var runtimeChecks = new Dictionary<string, ManagedHealthCheck>(
                    StringComparer.OrdinalIgnoreCase
                );
                if (healthWrapper is not null)
                {
                    foreach (ManagedHealthCheck runtimeCheck in healthWrapper.ApiHealthChecks)
                        runtimeChecks.TryAdd(runtimeCheck.Name, runtimeCheck);
                }
                var checks = new List<SupervisorApiHealthCheckSnapshot>(helperConfig.HealthChecks.Count);

                foreach (HealthCheckConfig checkConfig in helperConfig.HealthChecks)
                {
                    runtimeChecks.TryGetValue(checkConfig.Name, out ManagedHealthCheck? runtimeCheck);
                    checks.Add(new SupervisorApiHealthCheckSnapshot(
                        checkConfig.Name,
                        checkConfig.Enabled,
                        runtimeCheck?.ApiActive == true,
                        !checkConfig.Enabled ? "disabled" : runtimeCheck?.ApiStatus ?? "inactive",
                        runtimeCheck?.ApiDetail ?? "",
                        checkConfig.Type,
                        checkConfig.Protocol,
                        checkConfig.Port,
                        checkConfig.ActiveWhenProcess,
                        checkConfig.IntervalSeconds,
                        checkConfig.TimeoutSeconds,
                        checkConfig.FailureThreshold,
                        checkConfig.StartupDelaySeconds,
                        checkConfig.RestartOnFailure,
                        checkConfig.Parameters.ToArray(),
                        checkConfig.StaleSeconds
                    ));
                }

                if (helperConfig.MonitorResponsiveness)
                {
                    runtimeChecks.TryGetValue(
                        HealthCheckFactory.ResponsivenessCheckName,
                        out ManagedHealthCheck? responsivenessCheck
                    );
                    checks.Insert(0, new SupervisorApiHealthCheckSnapshot(
                        HealthCheckFactory.ResponsivenessCheckName,
                        Enabled: true,
                        responsivenessCheck?.ApiActive == true,
                        responsivenessCheck?.ApiStatus ?? "inactive",
                        responsivenessCheck?.ApiDetail ?? "",
                        Type: null,
                        Protocol: null,
                        Port: null,
                        ActiveWhenProcess: "",
                        IntervalSeconds: 10,
                        TimeoutSeconds: 3,
                        FailureThreshold: 3,
                        StartupDelaySeconds: 20,
                        RestartOnFailure: true,
                        Parameters: [],
                        StaleSeconds: null
                    ));
                }

                StartupMacroActionConfig[] actions = helperConfig.StartupMacros
                    .Select(CloneMacroAction)
                    .ToArray();
                string macroStatus = actions.Length == 0
                    ? "notConfigured"
                    : application?.ApiMacroError == true
                        ? "failed"
                        : application?.ApiMacroPending == true
                            ? "running"
                            : "idle";

                helpers.Add(new SupervisorApiHelperSnapshot(
                    Path.GetFileName(helperConfig.Path),
                    helperConfig.ResourceId,
                    helperConfig.Enabled,
                    active,
                    !helperConfig.Enabled ? "disabled" : active ? "active" : "inactive",
                    helperConfig.Path,
                    helperConfig.AppUri,
                    helperConfig.Arguments,
                    helperConfig.Restart,
                    helperConfig.EnsureClosedUntilNeeded,
                    helperConfig.LeaveRunningAfterProfileStops,
                    helperConfig.MinimizeAfterStart,
                    helperConfig.MonitorResponsiveness,
                    checks,
                    new SupervisorApiMacroSnapshot(actions.Length > 0, macroStatus, actions)
                ));
            }

            profiles.Add(new SupervisorApiProfileSnapshot(
                profileConfig.Name,
                profileConfig.ProfileId,
                profileConfig.Enabled,
                profileStatus,
                profileConfig.TriggerType == ProfileTriggerType.Process
                    ? "process"
                    : "bluetoothDevice",
                profileConfig.MonitorProcess,
                profileConfig.MonitorBluetoothDeviceId,
                helpers
            ));
        }

        return new SupervisorApiSnapshot(DateTime.UtcNow, paused, profiles);
    }

    private static StartupMacroActionConfig CloneMacroAction(StartupMacroActionConfig action) => new()
    {
        Type = action.Type,
        DelayMilliseconds = action.DelayMilliseconds,
        Keys = action.Keys?.ToList(),
        Monitor = action.Monitor,
        X = action.X,
        Y = action.Y,
        Width = action.Width,
        Height = action.Height
    };
}
