using AppSupervisor.Notifications;

namespace AppSupervisor;

/// <summary>
/// Selects the presentation targets used for notifications emitted by one helper application or service.
/// </summary>
public sealed class NotificationConfig
{
    /// <summary>
    /// Gets or sets the notification targets; an empty array intentionally suppresses visible notifications.
    /// </summary>
    public List<NotificationTarget> Target { get; set; } = [NotificationTarget.Popup];
}
