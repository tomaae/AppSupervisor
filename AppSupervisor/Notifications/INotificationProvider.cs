namespace AppSupervisor.Notifications;

/// <summary>
/// Delivers supervisor notifications through one presentation mechanism.
/// </summary>
internal interface INotificationProvider
{
    /// <summary>
    /// Gets the target handled by this provider.
    /// </summary>
    NotificationTarget Target { get; }

    /// <summary>
    /// Attempts to deliver one notification without allowing provider failures to escape into supervision.
    /// </summary>
    /// <param name="notification">The notification content to deliver.</param>
    /// <param name="cancellationToken">Cancels outstanding delivery during application shutdown.</param>
    /// <returns><see langword="true"/> when the provider accepted the notification.</returns>
    Task<bool> SendAsync(
        SupervisorNotification notification,
        CancellationToken cancellationToken);
}
