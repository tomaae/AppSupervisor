using AppSupervisor.Notifications;

namespace AppSupervisor.Tests;

/// <summary>
/// Verifies classic popup-dialog delivery without opening an interactive message box during tests.
/// </summary>
public sealed class PopupNotificationProviderTests
{
    /// <summary>Confirms a modal presenter cannot block the thread that publishes the popup.</summary>
    [Fact]
    public async Task SendAsync_BlockingDialogPresenter_ReturnsBeforeDialogCloses()
    {
        var presenterEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var releasePresenter = new ManualResetEventSlim();
        var provider = new PopupNotificationProvider(notification =>
        {
            presenterEntered.TrySetResult();
            releasePresenter.Wait(TimeSpan.FromSeconds(5));
        });
        var notification = CreateNotification(NotificationSeverity.Error);
        Task<bool>? delivery = null;

        try
        {
            delivery = Task.Run(
                async () => await provider.SendAsync(notification, CancellationToken.None)
            );
            await presenterEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Task completed = await Task.WhenAny(delivery, Task.Delay(TimeSpan.FromSeconds(1)));

            Assert.Same(delivery, completed);
            Assert.True(await delivery);
        }
        finally
        {
            releasePresenter.Set();

            if (delivery is not null)
                await delivery.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Confirms the popup target sends the original notification to its dialog presenter.</summary>
    [Fact]
    public async Task SendAsync_ActiveRequest_UsesDialogPresenter()
    {
        var presented = new TaskCompletionSource<SupervisorNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var provider = new PopupNotificationProvider(
            notification => presented.TrySetResult(notification)
        );
        var notification = CreateNotification(NotificationSeverity.Warning);

        bool delivered = await provider.SendAsync(notification, CancellationToken.None);
        SupervisorNotification actual = await presented.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(delivered);
        Assert.Same(notification, actual);
    }

    /// <summary>Confirms shutdown cancellation prevents a popup dialog thread from being started.</summary>
    [Fact]
    public async Task SendAsync_CancelledRequest_DoesNotShowDialog()
    {
        int presentationCount = 0;
        var provider = new PopupNotificationProvider(_ =>
            Interlocked.Increment(ref presentationCount)
        );
        var notification = CreateNotification(NotificationSeverity.Information);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        bool delivered = await provider.SendAsync(notification, cancellation.Token);

        Assert.False(delivered);
        Assert.Equal(0, Volatile.Read(ref presentationCount));
    }

    /// <summary>Confirms provider disposal permanently prevents any later popup thread from starting.</summary>
    [Fact]
    public async Task SendAsync_DisposedProvider_DoesNotShowDialog()
    {
        int presentationCount = 0;
        var provider = new PopupNotificationProvider(_ =>
            Interlocked.Increment(ref presentationCount)
        );
        provider.Dispose();

        bool delivered = await provider.SendAsync(
            CreateNotification(NotificationSeverity.Information),
            CancellationToken.None
        );

        Assert.False(delivered);
        Assert.Equal(0, Volatile.Read(ref presentationCount));
    }

    /// <summary>Creates one popup notification used by the provider-isolation tests.</summary>
    /// <param name="severity">The severity assigned to the notification.</param>
    /// <returns>A popup-only test notification.</returns>
    private static SupervisorNotification CreateNotification(NotificationSeverity severity)
    {
        return new SupervisorNotification(
            severity,
            "Test",
            "Test message",
            [NotificationTarget.Popup]
        );
    }
}
