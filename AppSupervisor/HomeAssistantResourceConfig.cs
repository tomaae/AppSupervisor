namespace AppSupervisor;

/// <summary>Configures one Home Assistant service call governed by a supervisor profile.</summary>
public sealed class HomeAssistantResourceConfig : ManagedResourceConfig
{
    /// <summary>Gets or sets the Home Assistant service identifier, such as switch.turn_on.</summary>
    public string Service { get; set; } = "";

    /// <summary>Gets or sets the entity targeted by the configured service.</summary>
    public string EntityId { get; set; } = "";

    /// <summary>Gets or sets the friendly entity name captured during discovery.</summary>
    public string EntityName { get; set; } = "";

    /// <summary>Gets or sets whether AppSupervisor confirms the requested state after a service call.</summary>
    public bool VerifyStateChange { get; set; }

    /// <summary>Gets or sets whether AppSupervisor checks and restores the requested state every minute.</summary>
    public bool Persistent { get; set; }

    /// <summary>Gets or sets Home Assistant resource-specific notification targets.</summary>
    public NotificationConfig Notifications { get; set; } = new();
}
