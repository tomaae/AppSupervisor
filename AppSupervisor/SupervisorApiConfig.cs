namespace AppSupervisor;

/// <summary>Configures the application-wide read-only Supervisor API.</summary>
public sealed class SupervisorApiConfig
{
    /// <summary>Gets or sets whether the loopback-only API listener is enabled.</summary>
    public bool Enabled { get; set; }
}
