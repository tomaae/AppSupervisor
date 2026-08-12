namespace AppSupervisor.Configuration;

/// <summary>
/// Defines the supported numeric ranges shared by configuration validation and the editor UI.
/// </summary>
internal static class ConfigurationLimits
{
    public const int MaximumTimeoutSeconds = 86_400;
    public const int MaximumHealthIntervalSeconds = 86_400;
    public const int MaximumHealthProbeTimeoutSeconds = 3_600;
    public const int MaximumHealthFailureThreshold = 1_000;
    public const int MaximumHealthStartupDelaySeconds = 86_400;
    public const int MaximumHealthStaleSeconds = 86_400;
    public const int MaximumProfileStartupDelayMilliseconds = 3_600_000;
    public const int MaximumWaitAfterStartupMilliseconds = 3_600_000;
}
