namespace AppSupervisor.Notifications;

/// <summary>
/// Identifies a supported destination for a supervisor notification.
/// </summary>
public enum NotificationTarget
{
    /// <summary>Shows a classic acknowledged Windows dialog.</summary>
    Popup,

    /// <summary>Shows a native Windows notification through the unelevated helper.</summary>
    Windows,

    /// <summary>Sends a notification through XSOverlay with Windows fallback.</summary>
    XsOverlay
}
