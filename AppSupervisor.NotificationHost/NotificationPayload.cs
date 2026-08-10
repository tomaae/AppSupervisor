namespace AppSupervisor.NotificationHost;

/// <summary>
/// Contains notification content received from the elevated supervisor process.
/// </summary>
internal sealed class NotificationPayload
{
    /// <summary>
    /// Gets or sets the numeric information, warning, or error severity.
    /// </summary>
    public int Severity { get; set; }

    /// <summary>
    /// Gets or sets the notification heading.
    /// </summary>
    public string Title { get; set; } = "";

    /// <summary>
    /// Gets or sets the detailed notification text.
    /// </summary>
    public string Message { get; set; } = "";

    /// <summary>
    /// Gets or sets the main executable path used for shortcut identity and icon display.
    /// </summary>
    public string MainExecutablePath { get; set; } = "";
}
