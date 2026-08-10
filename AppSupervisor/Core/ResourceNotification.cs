using AppSupervisor.Notifications;

namespace AppSupervisor.Core;

/// <summary>
/// Describes a resource-owned notification and its effect on recoverable tray error state.
/// </summary>
public sealed record ResourceNotification(
    string Key,
    NotificationSeverity Severity,
    string Title,
    string Message,
    IReadOnlyList<NotificationTarget> Targets,
    ResourceErrorState ErrorState = ResourceErrorState.None);

/// <summary>
/// Describes whether a resource notification sets, clears, or leaves recoverable error state unchanged.
/// </summary>
public enum ResourceErrorState
{
    /// <summary>Leaves the current recoverable error state unchanged.</summary>
    None,

    /// <summary>Marks the notification key as actively unhealthy.</summary>
    Set,

    /// <summary>Clears the notification key after confirmed recovery or deactivation.</summary>
    Clear
}
