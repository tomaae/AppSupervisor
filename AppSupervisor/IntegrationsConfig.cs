namespace AppSupervisor;

/// <summary>Contains application-wide integrations that are not owned by an individual supervisor profile.</summary>
public sealed class IntegrationsConfig
{
    /// <summary>Gets or sets SteamVR tracked-device monitoring.</summary>
    public SteamVrIntegrationConfig SteamVr { get; set; } = new();
}

