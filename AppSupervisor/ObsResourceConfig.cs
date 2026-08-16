using AppSupervisor.Notifications;

namespace AppSupervisor;

/// <summary>Configures one non-reversing OBS action governed by a supervisor profile.</summary>
public sealed class ObsResourceConfig : ManagedResourceConfig
{
    /// <summary>Gets or sets the OBS operation issued during profile activation.</summary>
    public ObsActionType Action { get; set; }

    /// <summary>Gets or sets the program scene to select or the scene containing a source.</summary>
    public string SceneName { get; set; } = "";

    /// <summary>Gets or sets the OBS input whose mute state is changed.</summary>
    public string InputName { get; set; } = "";

    /// <summary>Gets or sets the source name whose scene-item visibility is changed.</summary>
    public string SourceName { get; set; } = "";

    /// <summary>Gets or sets the requested input mute state.</summary>
    public bool Muted { get; set; }

    /// <summary>Gets or sets the requested scene-item visibility state.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>Gets or sets OBS action-specific notification targets.</summary>
    public NotificationConfig Notifications { get; set; } = new();
}
