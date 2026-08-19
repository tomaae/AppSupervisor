using AppSupervisor.Core;

namespace AppSupervisor;

/// <summary>
/// Handles check-specific notifications and recoverable health error state for the tray application.
/// </summary>
public partial class TrayApplicationContext
{
    private readonly Dictionary<HealthErrorIdentity, ActiveTrayError> _activeHealthErrors = [];

    private readonly record struct HealthErrorIdentity(SupervisorProfile Profile, IManagedResource Resource, string CheckKey);

    /// <summary>Applies a resource notification's error-state transition and publishes it through its own targets.</summary>
    /// <param name="profile">The supervisor profile that owns the resource.</param>
    /// <param name="resource">The helper application that owns the health check.</param>
    /// <param name="notification">The check-specific notification payload.</param>
    private void OnResourceNotificationRequested(
        SupervisorProfile profile,
        IManagedResource resource,
        ResourceNotification notification)
    {
        var errorKey = new HealthErrorIdentity(profile, resource, notification.Key);

        lock (_runtimeStateLock)
        {
            if (notification.ErrorState == ResourceErrorState.Set)
            {
                _activeHealthErrors[errorKey] = CreateActiveTrayError(
                    TrayTooltipText.CreateErrorSummary(
                        $"{profile.Name} health check failed",
                        notification.Message
                    )
                );
            }
            else if (notification.ErrorState == ResourceErrorState.Clear)
                _activeHealthErrors.Remove(errorKey);
        }

        UpdateTrayState();
        PublishNotification(
            notification.Severity,
            notification.Title,
            $"{profile.Name} - {notification.Message}",
            notification.Targets
        );
    }
}
