using AppSupervisor.Core;

namespace AppSupervisor;

/// <summary>
/// Handles check-specific notifications and recoverable health error state for the tray application.
/// </summary>
public partial class TrayApplicationContext
{
    private readonly HashSet<string> _activeHealthErrors =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Applies a resource notification's error-state transition and publishes it through its own targets.</summary>
    /// <param name="profile">The supervisor profile that owns the resource.</param>
    /// <param name="resource">The helper application that owns the health check.</param>
    /// <param name="notification">The check-specific notification payload.</param>
    private void OnResourceNotificationRequested(
        SupervisorProfile profile,
        IManagedResource resource,
        ResourceNotification notification)
    {
        string errorKey = string.Join(
            '\0',
            profile.Name,
            resource.DisplayName,
            notification.Key
        );

        if (notification.ErrorState == ResourceErrorState.Set)
            _activeHealthErrors.Add(errorKey);
        else if (notification.ErrorState == ResourceErrorState.Clear)
            _activeHealthErrors.Remove(errorKey);

        UpdateTrayState();
        PublishNotification(
            notification.Severity,
            notification.Title,
            $"{profile.Name} - {notification.Message}",
            notification.Targets
        );
    }
}
