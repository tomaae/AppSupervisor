using AppSupervisor.Core;

namespace AppSupervisor.Notifications;

/// <summary>
/// Creates notifications with an explicit ownership scope so helper settings cannot affect
/// another helper or an AppSupervisor-level message.
/// </summary>
internal static class ScopedNotificationFactory
{
    private static readonly NotificationTarget[] SystemTargets = [NotificationTarget.Popup];

    /// <summary>Creates an AppSupervisor-level notification with targets independent of helpers.</summary>
    internal static SupervisorNotification CreateSystem(
        NotificationSeverity severity,
        string title,
        string message)
    {
        return new SupervisorNotification(severity, title, message, SystemTargets);
    }

    /// <summary>Creates a lifecycle notification using only the owning resource's targets.</summary>
    internal static SupervisorNotification CreateResource(
        NotificationSeverity severity,
        string title,
        string message,
        IManagedResource resource)
    {
        return new SupervisorNotification(
            severity,
            title,
            message,
            resource.NotificationTargets
        );
    }

    /// <summary>Creates a check notification using only the targets stored by that check.</summary>
    internal static SupervisorNotification CreateCheck(
        string message,
        ResourceNotification notification)
    {
        return new SupervisorNotification(
            notification.Severity,
            notification.Title,
            message,
            notification.Targets
        );
    }
}
