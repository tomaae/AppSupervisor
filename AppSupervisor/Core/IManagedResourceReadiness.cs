namespace AppSupervisor.Core;

/// <summary>
/// Reports whether an activated resource has reached the started state required by dependent resources.
/// </summary>
internal interface IManagedResourceReadiness
{
    /// <summary>Checks whether the resource is currently started and usable as a startup dependency.</summary>
    /// <returns><see langword="true"/> when dependent resource activation may proceed.</returns>
    bool IsStarted();
}
