using AppSupervisor.Core;
using AppSupervisor.Notifications;
using AppSupervisor.Resources;
using AppSupervisor.ServiceControl;

namespace AppSupervisor.Tests;

/// <summary>
/// Verifies supervisor-profile state transitions independently of operating-system processes and services.
/// </summary>
public sealed class SupervisorProfileTests
{
    [Fact]
    public void Update_InactiveProfile_ObservesHelpersOnlyWhileConfigurationIsOpen()
    {
        int observations = 0;
        var application = new ManagedApplication(
            new ManagedApplicationConfig { Path = "inactive-helper.exe" },
            TimeSpan.Zero,
            shouldRemainRunning: null,
            processIdProvider: () =>
            {
                observations++;
                return new HashSet<int>();
            }
        );
        var serviceController = new RuntimeStatusServiceController
        {
            State = ServiceRuntimeState.Running
        };
        var service = new ManagedService(
            new ManagedServiceConfig { ServiceName = "InactiveService" },
            TimeSpan.Zero,
            _ => serviceController
        );
        using var profile = new SupervisorProfile(
            "Inactive",
            "root.exe",
            new FakeTrigger { Active = false },
            [application, service],
            TimeSpan.Zero
        );
        profile.InitializeResources();

        Assert.False(profile.Update(observeInactiveRuntimeStatus: false));
        Assert.Equal(0, observations);
        Assert.Equal(0, serviceController.StateQueries);
        Assert.Equal(
            ConfigurationResourceRuntimeStatus.Unknown,
            application.CachedRuntimeStatus
        );
        Assert.Equal(ConfigurationResourceRuntimeStatus.Unknown, service.CachedRuntimeStatus);

        Assert.False(profile.Update(observeInactiveRuntimeStatus: true));
        Assert.Equal(1, observations);
        Assert.Equal(1, serviceController.StateQueries);
        Assert.Equal(
            ConfigurationResourceRuntimeStatus.NotRunning,
            application.CachedRuntimeStatus
        );
        Assert.Equal(ConfigurationResourceRuntimeStatus.Running, service.CachedRuntimeStatus);

        Assert.False(profile.Update(observeInactiveRuntimeStatus: false));
        Assert.Equal(1, observations);
        Assert.Equal(1, serviceController.StateQueries);
    }

    /// <summary>
    /// Confirms activation, restart reporting, immediate recovery cancellation, and graceful deactivation sequencing.
    /// </summary>
    [Fact]
    public void Update_TriggerLifecycle_CoordinatesResourceOperations()
    {
        var trigger = new FakeTrigger { Active = true };
        var resource = new FakeResource([NotificationTarget.Popup]);
        using var profile = new SupervisorProfile(
            "Test",
            "root.exe",
            trigger,
            [resource],
            TimeSpan.Zero
        );
        IManagedResource? restartedResource = null;
        profile.ResourceRestarted += (_, restarted) => restartedResource = restarted;

        Assert.False(profile.Update());
        Assert.Equal(1, resource.ActivateCalls);

        profile.SuspendMonitoring();
        Assert.Equal(1, resource.SuspendCalls);

        resource.NextUpdate = ManagedResourceUpdate.Restarted;
        Assert.False(profile.Update());
        Assert.Same(resource, restartedResource);

        trigger.Active = false;
        Assert.True(profile.Update());
        Assert.True(profile.WaitingForCloseTimeout);
        Assert.False(profile.ResourceDeactivationPending);
        Assert.Equal(1, resource.CancelCalls);
        Assert.Equal(0, resource.DeactivateCalls);

        Assert.False(profile.Update());
        Assert.False(profile.WaitingForCloseTimeout);
        Assert.True(profile.ResourceDeactivationPending);
        Assert.Equal(1, resource.DeactivateCalls);

        Assert.False(profile.Update());
        Assert.False(profile.ResourceDeactivationPending);
        Assert.Equal(1, resource.SuperviseDeactivationCalls);
    }

    /// <summary>
    /// Confirms that one defective resource is reported without preventing later resources from being supervised.
    /// </summary>
    [Fact]
    public void Update_ResourceThrows_ReportsErrorAndContinuesProfile()
    {
        var trigger = new FakeTrigger { Active = true };
        var failingResource = new FakeResource([]) { ThrowOnSupervise = true };
        var healthyResource = new FakeResource([]);
        using var profile = new SupervisorProfile(
            "Isolation",
            "root.exe",
            trigger,
            [failingResource, healthyResource],
            TimeSpan.Zero
        );
        string? reportedError = null;
        profile.ErrorOccurred += (_, resource, message) =>
        {
            if (ReferenceEquals(resource, failingResource))
                reportedError = message;
        };

        Assert.False(profile.Update());
        Assert.False(profile.Update());

        Assert.Contains("Unexpected supervision failure", reportedError);
        Assert.Equal(1, failingResource.SuperviseCalls);
        Assert.Equal(1, healthyResource.SuperviseCalls);
    }

    /// <summary>
    /// Confirms that one resource disposal failure is reported without preventing later resources from being disposed.
    /// </summary>
    [Fact]
    public void Dispose_ResourceThrows_ReportsErrorAndContinuesCleanup()
    {
        var failingResource = new FakeResource([]) { ThrowOnDispose = true };
        var healthyResource = new FakeResource([]);
        var profile = new SupervisorProfile(
            "Cleanup",
            "root.exe",
            new FakeTrigger(),
            [failingResource, healthyResource],
            TimeSpan.Zero
        );
        string? reportedError = null;
        profile.ErrorOccurred += (_, resource, message) =>
        {
            if (ReferenceEquals(resource, failingResource))
                reportedError = message;
        };

        profile.Dispose();

        Assert.Contains("Unexpected disposal failure", reportedError);
        Assert.Equal(1, failingResource.DisposeCalls);
        Assert.Equal(1, healthyResource.DisposeCalls);
    }

    /// <summary>
    /// Supplies a mutable activation result for deterministic profile tests.
    /// </summary>
    private sealed class FakeTrigger : ITrigger
    {
        /// <summary>
        /// Gets or sets the activation result returned on the next profile tick.
        /// </summary>
        public bool Active { get; set; }

        /// <summary>
        /// Returns the test-controlled activation state.
        /// </summary>
        /// <returns>The current value of <see cref="Active"/>.</returns>
        public bool IsActive() => Active;
    }

    private sealed class RuntimeStatusServiceController : IWindowsServiceController
    {
        public ServiceRuntimeState State { get; set; }

        public int StateQueries { get; private set; }

        public void EnsureManualStartAndRequiredAccess()
        {
        }

        public ServiceRuntimeState GetState()
        {
            StateQueries++;
            return State;
        }

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Continue()
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Records lifecycle calls and exposes a test-controlled supervision result.
    /// </summary>
    private sealed class FakeResource : IManagedResource, IManagedResourceDeactivationState
    {
        /// <summary>
        /// Creates a fake resource with fixed notification targets.
        /// </summary>
        /// <param name="notificationTargets">The targets exposed to the supervisor profile.</param>
        public FakeResource(IReadOnlyList<NotificationTarget> notificationTargets)
        {
            NotificationTargets = notificationTargets;
        }

        public event Action<IManagedResource, string>? ErrorOccurred
        {
            add { }
            remove { }
        }

        public string DisplayName => "Fake resource";

        public IReadOnlyList<NotificationTarget> NotificationTargets { get; }

        public int ActivateCalls { get; private set; }

        public int CancelCalls { get; private set; }

        public int DeactivateCalls { get; private set; }

        public bool DeactivationPending { get; private set; }

        public int DisposeCalls { get; private set; }

        public int SuperviseDeactivationCalls { get; private set; }

        public int SuperviseCalls { get; private set; }

        public int SuspendCalls { get; private set; }

        public ManagedResourceUpdate NextUpdate { get; set; }

        public bool ThrowOnSupervise { get; set; }

        public bool ThrowOnDispose { get; set; }

        /// <summary>
        /// Records one activation request.
        /// </summary>
        public void Activate() => ActivateCalls++;

        /// <summary>
        /// Returns and clears the next configured supervision result.
        /// </summary>
        /// <returns>The test-controlled update result.</returns>
        public ManagedResourceUpdate Supervise()
        {
            SuperviseCalls++;

            if (ThrowOnSupervise)
                throw new InvalidOperationException("Simulated resource failure.");

            ManagedResourceUpdate update = NextUpdate;
            NextUpdate = ManagedResourceUpdate.None;
            return update;
        }

        /// <summary>
        /// Records one pending-recovery cancellation.
        /// </summary>
        public void CancelPendingRecovery() => CancelCalls++;

        /// <summary>
        /// Records one non-destructive monitoring suspension.
        /// </summary>
        public void SuspendMonitoring() => SuspendCalls++;

        /// <summary>
        /// Records one deactivation request.
        /// </summary>
        public void Deactivate()
        {
            DeactivateCalls++;
            DeactivationPending = true;
        }

        /// <summary>
        /// Records one deactivation-supervision tick.
        /// </summary>
        public void SuperviseDeactivation()
        {
            SuperviseDeactivationCalls++;
            DeactivationPending = false;
        }

        /// <summary>
        /// Releases the fake resource without external effects.
        /// </summary>
        public void Dispose()
        {
            DisposeCalls++;

            if (ThrowOnDispose)
                throw new InvalidOperationException("Simulated disposal failure.");
        }
    }
}
