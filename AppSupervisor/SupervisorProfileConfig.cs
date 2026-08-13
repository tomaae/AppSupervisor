namespace AppSupervisor;

/// <summary>Configures one monitored-process profile and its ordered managed resources.</summary>
public class SupervisorProfileConfig
{
    /// <summary>Gets or sets the unique user-facing profile name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Gets or sets whether the profile participates in supervision.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the executable filename whose running state activates the profile.</summary>
    public string MonitorProcess { get; set; } = "";

    /// <summary>Gets or sets how long the monitor process may remain absent before resources are closed.</summary>
    public int? CloseTimeoutSeconds { get; set; }

    /// <summary>Gets or sets how long an unexpectedly absent resource may remain absent before restart.</summary>
    public int? RestartTimeoutSeconds { get; set; }

    /// <summary>Gets or sets the helper applications supervised by this profile.</summary>
    public List<ManagedApplicationConfig> Applications { get; set; } = [];

    /// <summary>Gets or sets the Windows services supervised by this profile.</summary>
    public List<ManagedServiceConfig> Services { get; set; } = [];

    /// <summary>Gets or sets explicit nonblocking delays in the profile's startup sequence.</summary>
    public List<DelayResourceConfig> Delays { get; set; } = [];

    /// <summary>Gets or sets Home Assistant actions supervised by this profile.</summary>
    public List<HomeAssistantResourceConfig> HomeAssistantResources { get; set; } = [];
}
