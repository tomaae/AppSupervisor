namespace AppSupervisor;

/// <summary>
/// Describes the Task Scheduler properties that determine whether Windows startup is correctly configured.
/// </summary>
internal sealed record StartupTaskRegistration(
    string ExecutablePath,
    string WorkingDirectory,
    string PrincipalUserId,
    string TriggerUserId,
    bool TaskEnabled,
    bool LogonTriggerEnabled,
    bool HighestPrivileges,
    bool IgnoreNewInstances,
    int ActionCount,
    int TriggerCount
);
