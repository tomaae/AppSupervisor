namespace AppSupervisor;

/// <summary>Configures one Windows service supervised as part of a profile.</summary>
public class ManagedServiceConfig
{
    /// <summary>Gets or sets whether the service participates in supervision.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the internal Windows service name used by service-control APIs.</summary>
    public string ServiceName { get; set; } = "";

    /// <summary>Gets or sets whether an unexpectedly stopped service should restart.</summary>
    public bool Restart { get; set; } = true;

    /// <summary>Gets or sets service-specific notification targets.</summary>
    public NotificationConfig Notifications { get; set; } = new();
}
