namespace AppSupervisor.Core;

/// <summary>
/// Couples one runtime resource with its validated profile-local startup sequencing settings.
/// </summary>
internal sealed class ManagedResourceStartup
{
    /// <summary>Creates one ordered runtime startup entry.</summary>
    /// <param name="resource">The managed application or service.</param>
    /// <param name="resourceId">The stable profile-local resource identifier.</param>
    /// <param name="waitAfterStartupMilliseconds">The delay after activation before another entry may start.</param>
    /// <param name="dependencyResourceId">The optional earlier resource that must report started.</param>
    public ManagedResourceStartup(
        IManagedResource resource,
        string resourceId,
        int waitAfterStartupMilliseconds,
        string dependencyResourceId)
    {
        Resource = resource;
        ResourceId = resourceId;
        WaitAfterStartupMilliseconds = waitAfterStartupMilliseconds;
        DependencyResourceId = dependencyResourceId;
    }

    /// <summary>Gets the managed runtime resource.</summary>
    public IManagedResource Resource { get; }

    /// <summary>Gets the stable profile-local resource identifier.</summary>
    public string ResourceId { get; }

    /// <summary>Gets the nonblocking delay after activation.</summary>
    public int WaitAfterStartupMilliseconds { get; }

    /// <summary>Gets the optional earlier dependency identifier.</summary>
    public string DependencyResourceId { get; }
}
