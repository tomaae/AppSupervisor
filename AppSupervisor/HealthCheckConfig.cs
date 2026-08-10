namespace AppSupervisor;

/// <summary>
/// Configures one independently debounced network health check for a helper application.
/// </summary>
public sealed class HealthCheckConfig
{
    /// <summary>Gets or sets whether this check participates in supervision.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the unique, human-readable check name within its helper.</summary>
    public string Name { get; set; } = "";

    /// <summary>Gets or sets the required check type.</summary>
    public HealthCheckType? Type { get; set; }

    /// <summary>Gets or sets the transport for a listener check; unused by vrcosc.</summary>
    public ListenerProtocol? Protocol { get; set; }

    /// <summary>Gets or sets the local listener port; unused by vrcosc.</summary>
    public int? Port { get; set; }

    /// <summary>Gets or sets an optional process that gates a listener check; vrcosc is always gated by VRChat.exe.</summary>
    public string ActiveWhenProcess { get; set; } = "";

    /// <summary>Gets or sets how many seconds elapse between completed probe attempts.</summary>
    public int IntervalSeconds { get; set; } = 10;

    /// <summary>Gets or sets the maximum duration of one asynchronous probe.</summary>
    public int TimeoutSeconds { get; set; } = 3;

    /// <summary>Gets or sets the consecutive failed probes required before the check becomes unhealthy.</summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>Gets or sets the startup delay after activation before failed probes count.</summary>
    public int StartupDelaySeconds { get; set; } = 10;

    /// <summary>Gets or sets whether an unhealthy check requests a graceful all-instance helper restart.</summary>
    public bool RestartOnFailure { get; set; } = true;

    /// <summary>Gets or sets optional VRChat OSC parameter leaf names queried by a vrcosc check.</summary>
    public List<string> Parameters { get; set; } = [];

    /// <summary>Gets or sets the optional time that a majority of queried parameters may remain unchanged.</summary>
    public int? StaleSeconds { get; set; }

    /// <summary>Gets or sets the presentation targets used only by this health check.</summary>
    public NotificationConfig Notifications { get; set; } = new();
}
