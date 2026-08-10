namespace AppSupervisor.Notifications;

/// <summary>
/// Describes the importance and presentation style of a supervisor notification.
/// </summary>
public enum NotificationSeverity
{
    /// <summary>Reports ordinary status or confirmed recovery.</summary>
    Information,

    /// <summary>Reports a completed restart or another condition requiring attention.</summary>
    Warning,

    /// <summary>Reports a failed operation or unhealthy resource.</summary>
    Error
}
