using System.Threading.Channels;

namespace AppSupervisor.Notifications;

/// <summary>
/// Routes notifications to requested providers and applies XSOverlay-to-Windows failover.
/// </summary>
internal sealed class NotificationService : IDisposable
{
    private const int QueueCapacity = 256;
    private readonly IReadOnlyDictionary<NotificationTarget, INotificationProvider> _providers;
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly Channel<SupervisorNotification> _queue;
    private readonly Task _worker;
    private int _disposed;

    /// <summary>
    /// Creates a router from a set containing at most one provider for each notification target.
    /// </summary>
    /// <param name="providers">The available notification providers.</param>
    public NotificationService(IEnumerable<INotificationProvider> providers)
    {
        _providers = providers.ToDictionary(provider => provider.Target);
        _queue = Channel.CreateBounded<SupervisorNotification>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _worker = Task.Run(ProcessQueueAsync);
    }

    /// <summary>
    /// Starts best-effort notification delivery without blocking the WinForms supervision tick.
    /// </summary>
    /// <param name="notification">The provider-independent notification request.</param>
    public void Publish(SupervisorNotification notification)
    {
        if (Volatile.Read(ref _disposed) != 0 || notification.Targets.Count == 0)
            return;

        if (!_queue.Writer.TryWrite(notification))
        {
            SupervisorLog.WriteError(
                $"Notification queue is full; delivery was skipped: {notification.Title}.",
                new InvalidOperationException(
                    "The bounded notification queue reached capacity."
                )
            );
        }
    }

    /// <summary>
    /// Cancels outstanding provider work and prevents new notifications from being published.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _queue.Writer.TryComplete();
        _shutdownCancellation.Cancel();

        try
        {
            _worker.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException exception)
            when (exception.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
        }

        foreach (IDisposable provider in _providers.Values.OfType<IDisposable>())
            provider.Dispose();

        _shutdownCancellation.Dispose();
    }

    /// <summary>Drains notifications in order on one isolated worker so provider stalls cannot multiply.</summary>
    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (SupervisorNotification notification in
                _queue.Reader.ReadAllAsync(_shutdownCancellation.Token).ConfigureAwait(false))
            {
                try
                {
                    await PublishCoreAsync(
                        notification,
                        _shutdownCancellation.Token
                    ).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    // One provider failure cannot stop later queued notifications.
                }
            }
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
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
