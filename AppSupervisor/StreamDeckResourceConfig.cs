using AppSupervisor.Notifications;

namespace AppSupervisor;

/// <summary>Configures one activation-only action exposed by Stream Deck's MCP Actions profile.</summary>
public sealed class StreamDeckResourceConfig : ManagedResourceConfig
{
    /// <summary>Gets or sets the stable executable-action identifier assigned by Stream Deck.</summary>
    public string ActionId { get; set; } = "";

    /// <summary>Gets or sets the last discovered user-facing action title.</summary>
    public string ActionName { get; set; } = "";

    /// <summary>Gets or sets the optional title configured on the Stream Deck key.</summary>
    public string ActionTitle { get; set; } = "";

    /// <summary>Gets or sets whether Stream Deck reports exactly two action states.</summary>
    public bool IsSwitch { get; set; }

    /// <summary>Gets or sets whether a two-state action is invoked again on profile deactivation.</summary>
    public bool RestoreSwitchOnDeactivate { get; set; }

    /// <summary>Gets or sets Stream Deck action-specific notification targets.</summary>
    public NotificationConfig Notifications { get; set; } = new();
}
