using AppSupervisor.Health;
using AppSupervisor.Notifications;

namespace AppSupervisor.Tests;

/// <summary>
/// Verifies asynchronous health-check debounce, recovery, activation gating, and cancellation.
/// </summary>
public sealed class ManagedHealthCheckTests
{
    /// <summary>Confirms failures require their threshold while the first subsequent success recovers immediately.</summary>
    [Fact]
    public void Poll_ConfirmedFailureThenSuccess_RecoversImmediately()
    {
        var probe = new FakeProbe(
            HealthProbeResult.Failure("first"),
            HealthProbeResult.Failure("second"),
            HealthProbeResult.Success("recovered")
        );
        using ManagedHealthCheck check = CreateCheck(probe, new FakeCondition { Active = true });
        var failures = new List<string>();
        var recoveries = new List<string>();
        check.Failed += (_, detail) => failures.Add(detail);
        check.Recovered += (_, detail) => recoveries.Add(detail);
        DateTime now = DateTime.UtcNow;

        CompleteProbe(check, now);
        CompleteProbe(check, now.AddSeconds(1));
        CompleteProbe(check, now.AddSeconds(2));

        Assert.Equal(["second"], failures);
        Assert.Equal(["recovered"], recoveries);
    }

    /// <summary>Confirms an inactive prerequisite cancels a running probe and resets applicability.</summary>
    [Fact]
    public async Task Poll_ConditionBecomesInactive_CancelsProbe()
    {
        var condition = new FakeCondition { Active = true };
        var probe = new BlockingProbe();
        using ManagedHealthCheck check = CreateCheck(probe, condition);
        check.Poll(new HashSet<int> { 1 }, DateTime.UtcNow);

        condition.Active = false;
        check.Poll(new HashSet<int> { 1 }, DateTime.UtcNow.AddSeconds(1));
        await probe.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(probe.Cancelled.Task.IsCompletedSuccessfully);
    }

    /// <summary>Starts and completes one immediately resolved probe on two supervision ticks.</summary>
    /// <param name="check">The check to advance.</param>
    /// <param name="nowUtc">The probe start time.</param>
    private static void CompleteProbe(ManagedHealthCheck check, DateTime nowUtc)
    {
        var processIds = new HashSet<int> { 1 };
        check.Poll(processIds, nowUtc);
        check.Poll(processIds, nowUtc.AddMilliseconds(1));
    }

    /// <summary>Creates a zero-startup-delay, one-second interval test check.</summary>
    /// <param name="probe">The fake probe.</param>
    /// <param name="condition">The fake activation prerequisite.</param>
    /// <returns>A configured runtime health check.</returns>
    private static ManagedHealthCheck CreateCheck(
        IHealthProbe probe,
        IHealthCheckActivationCondition condition)
    {
        return new ManagedHealthCheck(
            new HealthCheckConfig
            {
                Name = "Test",
                Type = HealthCheckType.Listener,
                Protocol = ListenerProtocol.Tcp,
                Port = 12345,
                IntervalSeconds = 1,
                TimeoutSeconds = 5,
                FailureThreshold = 2,
                StartupDelaySeconds = 0,
                Notifications = new NotificationConfig
                {
                    Target = [NotificationTarget.Popup]
                }
            },
            probe,
            condition
        );
    }

    /// <summary>Returns a fixed sequence of immediate probe results.</summary>
    private sealed class FakeProbe : IHealthProbe
    {
        private readonly Queue<HealthProbeResult> _results;

        /// <summary>Creates a probe from an ordered result sequence.</summary>
        /// <param name="results">The results returned by successive checks.</param>
        public FakeProbe(params HealthProbeResult[] results)
        {
            _results = new Queue<HealthProbeResult>(results);
        }

        /// <summary>Returns the next configured result.</summary>
        /// <param name="ownerProcessIds">Unused fake process identifiers.</param>
        /// <param name="cancellationToken">Unused cancellation token.</param>
        /// <returns>The next immediate result.</returns>
        public Task<HealthProbeResult> CheckAsync(
            IReadOnlySet<int> ownerProcessIds,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_results.Dequeue());
        }

        /// <summary>Clears no state because this fake retains only its result sequence.</summary>
        public void Reset()
        {
        }

        /// <summary>Releases no resources.</summary>
        public void Dispose()
        {
        }
    }

    /// <summary>Waits until cancellation to prove lifecycle cancellation reaches an active probe.</summary>
    private sealed class BlockingProbe : IHealthProbe
    {
        /// <summary>Gets a signal completed when the probe observes cancellation.</summary>
        public TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Waits indefinitely until the supplied cancellation token fires.</summary>
        /// <param name="ownerProcessIds">Unused fake process identifiers.</param>
        /// <param name="cancellationToken">The lifecycle cancellation token.</param>
        /// <returns>A task that completes only through cancellation.</returns>
        public async Task<HealthProbeResult> CheckAsync(
            IReadOnlySet<int> ownerProcessIds,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return HealthProbeResult.Success();
            }
            finally
            {
                Cancelled.TrySetResult();
            }
        }

        /// <summary>Clears no retained state.</summary>
        public void Reset()
        {
        }

        /// <summary>Releases no resources.</summary>
        public void Dispose()
        {
        }
    }

    /// <summary>Supplies a mutable activation result.</summary>
    private sealed class FakeCondition : IHealthCheckActivationCondition
    {
        /// <summary>Gets or sets whether the check is applicable.</summary>
        public bool Active { get; set; }

        /// <summary>Returns the test-controlled applicability state.</summary>
        /// <returns>The value of <see cref="Active"/>.</returns>
        public bool IsActive() => Active;
    }
}
