namespace AppSupervisor;

/// <summary>
/// Provides startup ordering, dependency, delay, and enabled-state settings shared by applications and services.
/// </summary>
public abstract class ManagedResourceConfig
{
    /// <summary>Gets or sets the stable profile-local identifier used by startup dependencies.</summary>
    public string ResourceId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets or sets the zero-based cross-type startup position, or -1 when legacy order is unspecified.</summary>
    public int StartupOrder { get; set; } = -1;

    /// <summary>Gets or sets the nonblocking delay after issuing this resource's activation request.</summary>
    public int WaitAfterStartupMilliseconds { get; set; }

    /// <summary>Gets or sets the optional identifier of an earlier resource that must be started first.</summary>
    public string DependencyResourceId { get; set; } = "";

    /// <summary>Gets or sets whether the resource participates in supervision.</summary>
    public bool Enabled { get; set; } = true;
}
