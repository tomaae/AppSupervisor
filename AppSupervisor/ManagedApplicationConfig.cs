namespace AppSupervisor;

/// <summary>Configures one executable helper, its launch mechanism, and optional health monitoring.</summary>
public class ManagedApplicationConfig : ManagedResourceConfig
{
    /// <summary>Gets or sets the fully qualified helper executable path used as process identity.</summary>
    public string Path { get; set; } = "";

    /// <summary>Gets or sets an optional Steam or AppsFolder app URI used instead of direct executable launch.</summary>
    public string AppUri { get; set; } = "";

    /// <summary>Gets or sets command-line arguments passed only for direct executable launches.</summary>
    public string Arguments { get; set; } = "";

    /// <summary>Gets or sets the optional Windows package family used to refresh versioned executable paths.</summary>
    public string PackageFamilyName { get; set; } = "";

    /// <summary>Gets or sets the optional application identifier declared by a Windows package.</summary>
    public string PackageApplicationId { get; set; } = "";

    /// <summary>Gets or sets the executable path relative to the versioned Windows package directory.</summary>
    public string PackageExecutable { get; set; } = "";

    /// <summary>Gets or sets whether an unexpectedly exited helper should restart.</summary>
    public bool Restart { get; set; } = true;

    /// <summary>Gets or sets whether the helper should be closed while no referencing profile needs it.</summary>
    public bool EnsureClosedUntilNeeded { get; set; }

    /// <summary>Gets or sets whether the helper remains running when its owning profile becomes inactive.</summary>
    public bool LeaveRunningAfterProfileStops { get; set; }

    /// <summary>Gets or sets whether newly started helper windows should be minimized.</summary>
    public bool MinimizeAfterStart { get; set; }

    /// <summary>Gets or sets whether force-kill is permitted after all graceful close attempts fail.</summary>
    public bool ForceKillAfterCloseFailure { get; set; }

    /// <summary>Gets or sets whether the helper's owned windows are monitored for responsiveness.</summary>
    public bool MonitorResponsiveness { get; set; }

    /// <summary>Gets or sets helper-level notification targets.</summary>
    public NotificationConfig Notifications { get; set; } = new();

    /// <summary>Gets or sets independently debounced network checks owned by this helper.</summary>
    public List<HealthCheckConfig> HealthChecks { get; set; } = [];

    /// <summary>Gets or sets ordered actions executed after AppSupervisor confirms a helper launch.</summary>
    public List<StartupMacroActionConfig> StartupMacros { get; set; } = [];
}
