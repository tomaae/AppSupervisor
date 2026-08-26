using AppSupervisor.Core;
using AppSupervisor.Notifications;
using AppSupervisor.Resources;
using AppSupervisor.ServiceControl;

namespace AppSupervisor.Tests;

/// <summary>
/// Verifies service lifecycle behavior using an isolated Service Control Manager substitute.
/// </summary>
public sealed class ManagedServiceTests
{
    /// <summary>
    /// Confirms initialization checks Manual startup and activation starts a stopped service.
    /// </summary>
    [Fact]
    public void InitializeAndActivate_StoppedService_EnsuresManualAndStarts()
    {
        var controller = new FakeServiceController
        {
            State = ServiceRuntimeState.Stopped
        };
        using var service = CreateService(controller, TimeSpan.FromSeconds(20));

        service.Initialize();
        service.Activate();

        Assert.Equal(1, controller.EnsureManualCalls);
        Assert.Equal(1, controller.StartCalls);
    }

    [Fact]
    public void Readiness_ReusesStateObservedOrPredictedByLifecyclePass()
    {
        var controller = new FakeServiceController
        {
            State = ServiceRuntimeState.Stopped
        };
        using var service = CreateService(controller, TimeSpan.FromSeconds(20));
        service.Initialize();

        service.Activate();

        Assert.False(service.IsStarted());
        Assert.Equal(1, controller.StateQueries);
        controller.State = ServiceRuntimeState.Running;
        service.Supervise();
        Assert.True(service.IsStarted());
        Assert.Equal(2, controller.StateQueries);
    }

    [Fact]
    public void CachedRuntimeStatus_UsesObservedStateWithoutAdditionalServiceQueries()
    {
        var controller = new FakeServiceController
        {
            State = ServiceRuntimeState.Stopped
        };
        using var service = CreateService(controller, TimeSpan.FromSeconds(20));
        service.Initialize();

        Assert.Equal(ConfigurationResourceRuntimeStatus.Unknown, service.CachedRuntimeStatus);
        Assert.Equal(0, controller.StateQueries);

        service.Activate();
        Assert.Equal(ConfigurationResourceRuntimeStatus.Starting, service.CachedRuntimeStatus);
        Assert.Equal(1, controller.StateQueries);

        controller.State = ServiceRuntimeState.Running;
        service.Supervise();
        Assert.Equal(ConfigurationResourceRuntimeStatus.Running, service.CachedRuntimeStatus);
        Assert.Equal(2, controller.StateQueries);

        service.Deactivate();
        Assert.Equal(ConfigurationResourceRuntimeStatus.Stopping, service.CachedRuntimeStatus);
        Assert.Equal(3, controller.StateQueries);

        Assert.True(AdvanceUntilSettled(service));
        Assert.Equal(ConfigurationResourceRuntimeStatus.NotRunning, service.CachedRuntimeStatus);
        Assert.Equal(4, controller.StateQueries);
    }

    /// <summary>Confirms a service control handler cannot block the serialized supervisor worker.</summary>
    [Fact]
    public void Deactivate_SlowStopControl_ReturnsAndAdvancesWithoutWaiting()
    {
        using var stopEntered = new ManualResetEventSlim();
        using var releaseStop = new ManualResetEventSlim();
        var controller = new FakeServiceController
        {
            State = ServiceRuntimeState.Running,
            StopBehavior = () =>
            {
                stopEntered.Set();
                releaseStop.Wait();
            }
        };
        using var service = CreateService(controller, TimeSpan.FromSeconds(20));
        service.Initialize();

        service.Deactivate();

        Assert.True(stopEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(((IManagedResourceLifecycleWork)service).LifecycleWorkPending);
        Assert.Equal(
            ManagedResourceUpdate.None,
            ((IManagedResourceLifecycleWork)service).AdvanceLifecycle(DateTime.UtcNow)
        );
        Assert.Equal(1, controller.StateQueries);

        controller.State = ServiceRuntimeState.Stopped;
        releaseStop.Set();

        Assert.True(AdvanceUntilSettled(service));
        Assert.Equal(ConfigurationResourceRuntimeStatus.NotRunning, service.CachedRuntimeStatus);
    }

    /// <summary>Confirms disposal defers the native controller handle until a blocked stop returns.</summary>
    [Fact]
    public void Dispose_StopControlStillRunning_DefersControllerDisposal()
    {
        using var stopEntered = new ManualResetEventSlim();
        using var releaseStop = new ManualResetEventSlim();
        var controller = new FakeServiceController
        {
            State = ServiceRuntimeState.Running,
            StopBehavior = () =>
            {
                stopEntered.Set();
                releaseStop.Wait();
            }
        };
        var service = CreateService(controller, TimeSpan.FromSeconds(20));
        service.Initialize();
        service.Deactivate();
        Assert.True(stopEntered.Wait(TimeSpan.FromSeconds(2)));

        service.Dispose();

        Assert.False(controller.Disposed);
        releaseStop.Set();
        Assert.True(SpinWait.SpinUntil(
            () => controller.Disposed,
            TimeSpan.FromSeconds(2)
        ));
    }

    /// <summary>Confirms a delayed native stop failure is reported from a later lifecycle pass.</summary>
    [Fact]
    public void AdvanceLifecycle_StopControlFails_ReportsServiceError()
    {
        var controller = new FakeServiceController
        {
            State = ServiceRuntimeState.Running,
            StopBehavior = () => throw new TimeoutException("Simulated service timeout.")
        };
        using var service = CreateService(controller, TimeSpan.FromSeconds(20));
        string? error = null;
        service.ErrorOccurred += (_, message) => error = message;
        service.Initialize();
        service.Deactivate();

        Assert.True(AdvanceUntilSettled(service));
        Assert.Contains("Could not stop service 'Test Service'", error);
        Assert.Contains("Simulated service timeout", error);
    }

    /// <summary>
    /// Confirms an unexpectedly stopped service restarts only after its restart timeout has elapsed.
    /// </summary>
    [Fact]
    public void Supervise_StoppedServiceAfterZeroTimeout_ReportsRestart()
    {
        var controller = new FakeServiceController
        {
            State = ServiceRuntimeState.Stopped
        };
        using var service = CreateService(controller, TimeSpan.Zero);
        service.Initialize();

        Assert.Equal(ManagedResourceUpdate.None, service.Supervise());
        Assert.Equal(ManagedResourceUpdate.Restarted, service.Supervise());
        Assert.Equal(1, controller.StartCalls);
    }

    [Fact]
    public void AutomaticStartFailures_StopAfterFiveAttemptsAndResetWhenRunning()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var controller = new FakeServiceController
        {
            State = ServiceRuntimeState.Stopped,
            StartBehavior = () => throw new InvalidOperationException("Start rejected.")
        };
        using var service = CreateService(controller, TimeSpan.Zero, time);
        var errors = new List<string>();
        service.ErrorOccurred += (_, message) => errors.Add(message);
        service.Initialize();
        service.Activate();

        Assert.Equal(1, controller.StartCalls);
        service.Supervise();
        for (int attempt = 2; attempt <= AutomaticRecoveryBudget.MaximumAttempts; attempt++)
        {
            time.Advance(AutomaticRecoveryBudget.RetryDelay);
            service.Supervise();
            Assert.Equal(attempt, controller.StartCalls);
        }

        time.Advance(TimeSpan.FromMinutes(1));
        service.Supervise();
        Assert.Equal(5, controller.StartCalls);
        Assert.Contains("attempt 5 of 5", errors.Last());

        controller.StartBehavior = () => controller.State = ServiceRuntimeState.Running;
        controller.State = ServiceRuntimeState.Running;
        service.Supervise();
        controller.State = ServiceRuntimeState.Stopped;
        service.Supervise();
        service.Supervise();

        Assert.Equal(6, controller.StartCalls);
    }

    /// <summary>
    /// Creates a service resource using a supplied fake controller.
    /// </summary>
    /// <param name="controller">The isolated service controller.</param>
    /// <param name="restartTimeout">The restart timeout period used by the test.</param>
    /// <returns>A managed service bound to the fake controller.</returns>
    private static ManagedService CreateService(
        FakeServiceController controller,
        TimeSpan restartTimeout,
        TimeProvider? timeProvider = null)
    {
        return new ManagedService(
            new ManagedServiceConfig
            {
                ServiceName = "Test Service",
                Restart = true,
                Notifications = new NotificationConfig
                {
                    Target = [NotificationTarget.Popup]
                }
            },
            restartTimeout,
            _ => controller,
            timeProvider
        );
    }

    /// <summary>Advances the service lifecycle until no asynchronous command or transition remains.</summary>
    private static bool AdvanceUntilSettled(ManagedService service)
    {
        var lifecycle = (IManagedResourceLifecycleWork)service;
        return SpinWait.SpinUntil(
            () =>
            {
                lifecycle.AdvanceLifecycle(DateTime.UtcNow);
                return !lifecycle.LifecycleWorkPending;
            },
            TimeSpan.FromSeconds(2)
        );
    }

    /// <summary>
    /// Records Windows service operations while exposing a test-controlled state.
    /// </summary>
    private sealed class FakeServiceController : IWindowsServiceController
    {
        private int _disposed;

        public ServiceRuntimeState State { get; set; }

        public Action? StopBehavior { get; set; }

        public Action? StartBehavior { get; set; }

        public bool Disposed => Volatile.Read(ref _disposed) != 0;

        public int EnsureManualCalls { get; private set; }

        public int StartCalls { get; private set; }

        public int StateQueries { get; private set; }

        /// <summary>
        /// Records Manual-start enforcement and permission verification.
        /// </summary>
        public void EnsureManualStartAndRequiredAccess() => EnsureManualCalls++;

        /// <summary>
        /// Returns the state selected by the test.
        /// </summary>
        /// <returns>The current fake service state.</returns>
        public ServiceRuntimeState GetState()
        {
            StateQueries++;
            return State;
        }

        /// <summary>
        /// Records a start request.
        /// </summary>
        public void Start()
        {
            StartCalls++;
            StartBehavior?.Invoke();
        }

        /// <summary>
        /// Changes the fake service to stopped.
        /// </summary>
        public void Stop()
        {
            if (StopBehavior is not null)
            {
                StopBehavior();
                return;
            }

            State = ServiceRuntimeState.Stopped;
        }

        /// <summary>
        /// Changes the fake service to running.
        /// </summary>
        public void Continue() => State = ServiceRuntimeState.Running;

        /// <summary>
        /// Releases the fake controller without external effects.
        /// </summary>
        public void Dispose()
        {
            Volatile.Write(ref _disposed, 1);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _utcNow = initial;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }
}
