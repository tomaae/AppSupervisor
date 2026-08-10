namespace AppSupervisor.Core;

/// <summary>
/// Describes the externally relevant result of one managed-resource supervision cycle.
/// </summary>
public enum ManagedResourceUpdate
{
    /// <summary>No externally reportable lifecycle change occurred.</summary>
    None,

    /// <summary>The resource started a replacement after an unexpected stop.</summary>
    Restarted
}
