namespace AppSupervisor;

/// <summary>Contains the complete profile and application-wide integration configuration.</summary>
public sealed class AppSupervisorConfig
{
    /// <summary>Gets or sets process- or Bluetooth-activated supervisor profiles.</summary>
    public List<SupervisorProfileConfig> Profiles { get; set; } = [];

    /// <summary>Gets or sets integrations shared across every profile.</summary>
    public IntegrationsConfig Integrations { get; set; } = new();
}
