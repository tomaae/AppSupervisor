namespace AppSupervisor;

/// <summary>
/// Selects the kind of network health signal evaluated for a managed helper application.
/// </summary>
public enum HealthCheckType
{
    /// <summary>Checks that the helper owns the configured listening TCP or UDP port.</summary>
    Listener,

    /// <summary>Discovers VRChat through OSCQuery and optionally checks live OSC parameter freshness.</summary>
    Vrcosc
}
