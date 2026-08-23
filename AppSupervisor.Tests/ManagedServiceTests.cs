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

        service.SuperviseDeactivation();
        Assert.Equal(ConfigurationResourceRuntimeStatus.NotRunning, service.CachedRuntimeStatus);
        Assert.Equal(4, controller.StateQueries);
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

    /// <summary>
    /// Creates a service resource using a supplied fake controller.
    /// </summary>
    /// <param name="controller">The isolated service controller.</param>
    /// <param name="restartTimeout">The restart timeout period used by the test.</param>
    /// <returns>A managed service bound to the fake controller.</returns>
    private static ManagedService CreateService(
        FakeServiceController controller,
        TimeSpan restartTimeout)
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
            _ => controller
        );
    }

    /// <summary>
    /// Records Windows service operations while exposing a test-controlled state.
    /// </summary>
    private sealed class FakeServiceController : IWindowsServiceController
    {
        public ServiceRuntimeState State { get; set; }

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
        public void Start() => StartCalls++;

        /// <summary>
        /// Changes the fake service to stopped.
        /// </summary>
        public void Stop() => State = ServiceRuntimeState.Stopped;

        /// <summary>
        /// Changes the fake service to running.
        /// </summary>
        public void Continue() => State = ServiceRuntimeState.Running;

        /// <summary>
        /// Releases the fake controller without external effects.
        /// </summary>
        public void Dispose()
        {
        }
    }
}
