namespace AppSupervisor.Notifications;

/// <summary>
/// Routes notifications to requested providers and applies XSOverlay-to-Windows failover.
/// </summary>
internal sealed class NotificationService : IDisposable
{
    private readonly IReadOnlyDictionary<NotificationTarget, INotificationProvider> _providers;
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private bool _disposed;

    /// <summary>
    /// Creates a router from a set containing at most one provider for each notification target.
    /// </summary>
    /// <param name="providers">The available notification providers.</param>
    public NotificationService(IEnumerable<INotificationProvider> providers)
    {
        _providers = providers.ToDictionary(provider => provider.Target);
    }

    /// <summary>
    /// Starts best-effort notification delivery without blocking the WinForms supervision tick.
    /// </summary>
    /// <param name="notification">The provider-independent notification request.</param>
    public void Publish(SupervisorNotification notification)
    {
        if (_disposed || notification.Targets.Count == 0)
            return;

        _ = PublishSafelyAsync(notification, _shutdownCancellation.Token);
    }

    /// <summary>
    /// Cancels outstanding provider work and prevents new notifications from being published.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _shutdownCancellation.Cancel();

        foreach (IDisposable provider in _providers.Values.OfType<IDisposable>())
            provider.Dispose();

        _shutdownCancellation.Dispose();
    }

    /// <summary>
    /// Contains all provider exceptions so notification delivery can never stop supervision.
    /// </summary>
    /// <param name="notification">The notification request being delivered.</param>
    /// <param name="cancellationToken">Cancels delivery during shutdown.</param>
    private async Task PublishSafelyAsync(
        SupervisorNotification notification,
        CancellationToken cancellationToken)
    {
        try
        {
            await PublishCoreAsync(notification, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Notification providers are intentionally best-effort and must never fail supervision.
        }
    }

    /// <summary>
    /// Delivers distinct explicit targets and sends Windows once when XSOverlay delivery fails.
    /// </summary>
    /// <param name="notification">The notification request being delivered.</param>
    /// <param name="cancellationToken">Cancels delivery during shutdown.</param>
    private async Task PublishCoreAsync(
        SupervisorNotification notification,
        CancellationToken cancellationToken)
    {
        bool windowsRequested = notification.Targets.Contains(NotificationTarget.Windows);

        foreach (NotificationTarget target in notification.Targets)
        {
            if (target == NotificationTarget.XsOverlay)
                continue;

            await TrySendAsync(target, notification, cancellationToken).ConfigureAwait(false);
        }

        if (!notification.Targets.Contains(NotificationTarget.XsOverlay))
            return;

        bool xsOverlayDelivered = await TrySendAsync(
            NotificationTarget.XsOverlay,
            notification,
            cancellationToken
        ).ConfigureAwait(false);

        if (!xsOverlayDelivered && !windowsRequested)
        {
            await TrySendAsync(
                NotificationTarget.Windows,
                notification,
                cancellationToken
            ).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Invokes one provider and converts missing providers or delivery exceptions into a failed result.
    /// </summary>
    /// <param name="target">The provider target to invoke.</param>
    /// <param name="notification">The notification request being delivered.</param>
    /// <param name="cancellationToken">Cancels delivery during shutdown.</param>
    /// <returns><see langword="true"/> when the provider accepted the notification.</returns>
    private async Task<bool> TrySendAsync(
        NotificationTarget target,
        SupervisorNotification notification,
        CancellationToken cancellationToken)
    {
        if (!_providers.TryGetValue(target, out INotificationProvider? provider))
            return false;

        try
        {
            return await provider.SendAsync(notification, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
