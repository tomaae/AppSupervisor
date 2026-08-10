using System.Text;
using System.Text.Json;

namespace AppSupervisor.Notifications;

/// <summary>
/// Requests a native Windows notification through the separate unelevated notification host.
/// </summary>
internal sealed class WindowsNotificationProvider : INotificationProvider
{
    private readonly string _mainExecutablePath;
    private readonly string _notificationHostPath;

    /// <summary>
    /// Creates a Windows provider bound to the main executable and its notification host.
    /// </summary>
    /// <param name="mainExecutablePath">The full AppSupervisor executable path.</param>
    /// <param name="notificationHostPath">The full unelevated helper executable path.</param>
    public WindowsNotificationProvider(
        string mainExecutablePath,
        string notificationHostPath)
    {
        _mainExecutablePath = mainExecutablePath;
        _notificationHostPath = notificationHostPath;
    }

    /// <summary>
    /// Gets the Windows target handled by this provider.
    /// </summary>
    public NotificationTarget Target => NotificationTarget.Windows;

    /// <summary>
    /// Encodes notification content and asks Explorer to launch the unelevated host.
    /// </summary>
    /// <param name="notification">The notification content to display.</param>
    /// <param name="cancellationToken">Cancels delivery during application shutdown.</param>
    /// <returns>A completed result indicating whether Windows accepted the helper launch.</returns>
    public Task<bool> SendAsync(
        SupervisorNotification notification,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested ||
            !File.Exists(_notificationHostPath))
        {
            return Task.FromResult(false);
        }

        var payload = new WindowsNotificationPayload
        {
            Severity = notification.Severity,
            Title = notification.Title,
            Message = notification.Message,
            MainExecutablePath = _mainExecutablePath
        };

        string json = JsonSerializer.Serialize(payload);
        string encodedPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        return Task.FromResult(UnelevatedProcessLauncher.TryLaunch(
            _notificationHostPath,
            $"\"{encodedPayload}\""
        ));
    }
}
