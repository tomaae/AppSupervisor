namespace AppSupervisor.Core;

/// <summary>
/// Exposes resource-specific notifications beyond the base lifecycle contract.
/// </summary>
public interface IResourceNotificationSource
{
    /// <summary>Occurs when a resource needs to publish a targeted health or recovery notification.</summary>
    event Action<IManagedResource, ResourceNotification>? NotificationRequested;
}
