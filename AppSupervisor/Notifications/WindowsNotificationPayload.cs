namespace AppSupervisor.Notifications;

/// <summary>
/// Contains serializable notification data passed to the unelevated notification host.
/// </summary>
internal sealed class WindowsNotificationPayload
{
    /// <summary>
    /// Gets or sets the notification severity.
    /// </summary>
    public NotificationSeverity Severity { get; set; }

    /// <summary>
    /// Gets or sets the notification heading.
    /// </summary>
    public string Title { get; set; } = "";

    /// <summary>
    /// Gets or sets the detailed notification text.
    /// </summary>
    public string Message { get; set; } = "";

    /// <summary>
    /// Gets or sets the main executable path used by the notification host for desktop identity.
    /// </summary>
    public string MainExecutablePath { get; set; } = "";
}
