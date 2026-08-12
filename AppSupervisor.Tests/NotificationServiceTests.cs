using AppSupervisor.Notifications;

namespace AppSupervisor.Tests;

/// <summary>
/// Verifies provider routing and XSOverlay fallback independently of real desktop notification systems.
/// </summary>
public sealed class NotificationServiceTests
{
    /// <summary>
    /// Confirms that failed XSOverlay delivery falls back to Windows exactly once.
    /// </summary>
    [Fact]
    public async Task Publish_XsOverlayFailure_FallsBackToWindows()
    {
        var windows = new FakeProvider(NotificationTarget.Windows, succeeds: true);
        var xsOverlay = new FakeProvider(NotificationTarget.XsOverlay, succeeds: false);
        using var service = new NotificationService([windows, xsOverlay]);

        service.Publish(CreateNotification([NotificationTarget.XsOverlay]));

        await windows.WaitForCallAsync();
        Assert.Equal(1, xsOverlay.CallCount);
        Assert.Equal(1, windows.CallCount);
    }

    /// <summary>
    /// Confirms that an explicit Windows target is not delivered twice when XSOverlay also fails.
    /// </summary>
    [Fact]
    public async Task Publish_WindowsAndFailedXsOverlay_DeliversWindowsOnce()
    {
        var windows = new FakeProvider(NotificationTarget.Windows, succeeds: true);
        var xsOverlay = new FakeProvider(NotificationTarget.XsOverlay, succeeds: false);
        using var service = new NotificationService([windows, xsOverlay]);

        service.Publish(CreateNotification(
            [NotificationTarget.Windows, NotificationTarget.XsOverlay]
        ));

        await xsOverlay.WaitForCallAsync();
        await Task.Yield();
        Assert.Equal(1, windows.CallCount);
        Assert.Equal(1, xsOverlay.CallCount);
    }

    /// <summary>
    /// Confirms successful XSOverlay delivery does not invoke the Windows fallback.
    /// </summary>
    [Fact]
    public async Task Publish_XsOverlaySuccess_DoesNotUseWindowsFallback()
    {
        var windows = new FakeProvider(NotificationTarget.Windows, succeeds: true);
        var xsOverlay = new FakeProvider(NotificationTarget.XsOverlay, succeeds: true);
        using var service = new NotificationService([windows, xsOverlay]);

        service.Publish(CreateNotification([NotificationTarget.XsOverlay]));

        await xsOverlay.WaitForCallAsync();
        await Task.Yield();
        Assert.Equal(0, windows.CallCount);
    }

    /// <summary>
    /// Confirms a stalled provider never blocks publishers and receives queued work without overlap.
    /// </summary>
    [Fact]
    public async Task Publish_StalledProvider_ReturnsImmediatelyAndSerializesDelivery()
    {
        var provider = new BlockingProvider(expectedCalls: 3);
        using var service = new NotificationService([provider]);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        service.Publish(CreateNotification([NotificationTarget.XsOverlay]));
        service.Publish(CreateNotification([NotificationTarget.XsOverlay]));
        service.Publish(CreateNotification([NotificationTarget.XsOverlay]));

        stopwatch.Stop();
        await provider.WaitForFirstCallAsync();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(100),
            $"Publishing blocked for {stopwatch.ElapsedMilliseconds} ms."
        );

        provider.Release();
        await provider.WaitForAllCallsAsync();
        Assert.Equal(1, provider.MaximumConcurrency);
    }

    /// <summary>
    /// Creates one provider-independent warning used by routing tests.
    /// </summary>
    /// <param name="targets">The destinations requested by the test.</param>
    /// <returns>A warning notification with the requested targets.</returns>
    private static SupervisorNotification CreateNotification(
        IEnumerable<NotificationTarget> targets)
    {
        return new SupervisorNotification(
            NotificationSeverity.Warning,
            "Test",
            "Test notification",
            targets
        );
    }

    /// <summary>
    /// Records provider calls and returns a fixed delivery result.
    /// </summary>
    private sealed class FakeProvider : INotificationProvider
    {
        private readonly bool _succeeds;
        private readonly TaskCompletionSource _called = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _callCount;

        /// <summary>
        /// Creates a provider for one target with a fixed success result.
        /// </summary>
        /// <param name="target">The destination handled by the provider.</param>
        /// <param name="succeeds">The result returned for every delivery.</param>
        public FakeProvider(NotificationTarget target, bool succeeds)
        {
            Target = target;
            _succeeds = succeeds;
        }

        public NotificationTarget Target { get; }

        public int CallCount => Volatile.Read(ref _callCount);

        /// <summary>
        /// Records a delivery and returns the configured result.
        /// </summary>
        /// <param name="notification">The delivered notification.</param>
        /// <param name="cancellationToken">The shutdown cancellation token.</param>
        /// <returns>The fixed provider result.</returns>
        public Task<bool> SendAsync(
            SupervisorNotification notification,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            _called.TrySetResult();
            return Task.FromResult(_succeeds);
        }

        /// <summary>
        /// Waits for the provider to be called and fails promptly if routing stalls.
        /// </summary>
        public async Task WaitForCallAsync()
        {
            await _called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Blocks the first provider call and records delivery concurrency across queued requests.</summary>
    private sealed class BlockingProvider : INotificationProvider
    {
        private readonly int _expectedCalls;
        private readonly TaskCompletionSource _firstCall = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _allCalls = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _activeCalls;
        private int _callCount;
        private int _maximumConcurrency;

        /// <summary>Creates a provider that completes after the requested number of calls.</summary>
        /// <param name="expectedCalls">The total calls expected from the queue.</param>
        public BlockingProvider(int expectedCalls)
        {
            _expectedCalls = expectedCalls;
        }

        public NotificationTarget Target => NotificationTarget.XsOverlay;

        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        /// <summary>Waits until released, then records completion without overlapping another delivery.</summary>
        public async Task<bool> SendAsync(
            SupervisorNotification notification,
            CancellationToken cancellationToken)
        {
            int concurrency = Interlocked.Increment(ref _activeCalls);
            UpdateMaximumConcurrency(concurrency);
            _firstCall.TrySetResult();

            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                return true;
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);

                if (Interlocked.Increment(ref _callCount) == _expectedCalls)
                    _allCalls.TrySetResult();
            }
        }

        public Task WaitForFirstCallAsync()
            => _firstCall.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public Task WaitForAllCallsAsync()
            => _allCalls.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Release() => _release.TrySetResult();

        /// <summary>Atomically records the highest simultaneous provider-call count.</summary>
        private void UpdateMaximumConcurrency(int concurrency)
        {
            int observed;

            do
            {
                observed = Volatile.Read(ref _maximumConcurrency);
                if (observed >= concurrency)
                    return;
            }
            while (Interlocked.CompareExchange(
                ref _maximumConcurrency,
                concurrency,
                observed
            ) != observed);
        }
    }
}
