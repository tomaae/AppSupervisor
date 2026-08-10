namespace AppSupervisor.Notifications;

/// <summary>
/// Contains provider-independent notification content and the requested presentation targets.
/// </summary>
public sealed class SupervisorNotification
{
    /// <summary>
    /// Creates an immutable notification request.
    /// </summary>
    /// <param name="severity">The importance and visual style of the notification.</param>
    /// <param name="title">The short notification heading.</param>
    /// <param name="message">The detailed notification text.</param>
    /// <param name="targets">The requested presentation targets.</param>
    public SupervisorNotification(
        NotificationSeverity severity,
        string title,
        string message,
        IEnumerable<NotificationTarget> targets)
    {
        Severity = severity;
        Title = title;
        Message = message;
        Targets = targets.Distinct().ToArray();
    }

    /// <summary>
    /// Gets the importance and presentation style.
    /// </summary>
    public NotificationSeverity Severity { get; }

    /// <summary>
    /// Gets the short notification heading.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the detailed notification text.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Gets the distinct requested presentation targets.
    /// </summary>
    public IReadOnlyList<NotificationTarget> Targets { get; }
}
