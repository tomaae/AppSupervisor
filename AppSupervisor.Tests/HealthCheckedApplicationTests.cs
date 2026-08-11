using AppSupervisor.Core;
using AppSupervisor.Health;
using AppSupervisor.Notifications;
using AppSupervisor.Resources;

namespace AppSupervisor.Tests;

/// <summary>
/// Verifies helper recovery initiated by confirmed application health failures.
/// </summary>
public sealed class HealthCheckedApplicationTests
{
    /// <summary>Confirms pausing a health-aware helper always reaches its underlying application lifecycle.</summary>
    [Fact]
    public void SuspendMonitoring_ActiveHelper_SuspendsApplication()
    {
        var application = new FakeApplicationLifecycle { Running = true };
        using HealthCheckedApplication wrapper = CreateWrapper(application);

        wrapper.SuspendMonitoring();

        Assert.Equal(1, application.SuspendCalls);
    }

    /// <summary>
    /// Confirms a helper that exits before the next tick has its pending close finalized before one replacement starts.
    /// </summary>
    [Fact]
    public void Supervise_HealthRestart_HelperAlreadyGone_FinalizesCloseAndStartsReplacement()
    {
        var application = new FakeApplicationLifecycle
        {
            Running = true,
            CompleteLaunchImmediately = true
        };
        using HealthCheckedApplication wrapper = CreateWrapper(application);
        var notifications = new List<ResourceNotification>();
        wrapper.NotificationRequested += (_, notification) => notifications.Add(notification);

        ConfirmHealthFailure(wrapper);
        Assert.Equal(1, application.DeactivateCalls);
        Assert.True(application.CloseOperationPending);

        application.Running = false;
        wrapper.Supervise();

        Assert.Equal(1, application.SuperviseDeactivationCalls);
        Assert.False(application.CloseOperationPending);
        Assert.Equal(1, application.ActivateCalls);
        Assert.True(application.Running);
        Assert.Contains(
            notifications,
            notification => notification.Severity == NotificationSeverity.Warning &&
                notification.Title == "Resource restarted"
        );
    }

    /// <summary>
    /// Confirms an asynchronous URI launch is requested once and reported only after its process becomes discoverable.
    /// </summary>
    [Fact]
    public void Supervise_HealthRestart_DelayedLaunch_DoesNotIssueDuplicateStart()
    {
        var application = new FakeApplicationLifecycle
        {
            Running = true,
            CompleteLaunchImmediately = false
        };
        using HealthCheckedApplication wrapper = CreateWrapper(application);
        var notifications = new List<ResourceNotification>();
        wrapper.NotificationRequested += (_, notification) => notifications.Add(notification);

        ConfirmHealthFailure(wrapper);
        application.Running = false;

        wrapper.Supervise();
        wrapper.Supervise();

        Assert.Equal(1, application.ActivateCalls);
        Assert.DoesNotContain(
            notifications,
            notification => notification.Title == "Resource restarted"
        );

        application.Running = true;
        wrapper.Supervise();

        Assert.Equal(1, application.ActivateCalls);
        Assert.Contains(
            notifications,
            notification => notification.Title == "Resource restarted"
        );
    }

    /// <summary>Confirms a shared-helper close guard reports blocked recovery instead of a false restart.</summary>
    [Fact]
    public void Supervise_HealthRestartBlockedBySharedProfile_DoesNotReportRestart()
    {
        var application = new FakeApplicationLifecycle
        {
            Running = true,
            BlockDeactivation = true
        };
        using HealthCheckedApplication wrapper = CreateWrapper(application);
        var notifications = new List<ResourceNotification>();
        wrapper.NotificationRequested += (_, notification) => notifications.Add(notification);

        ConfirmHealthFailure(wrapper);
        wrapper.Supervise();

        Assert.Equal(1, application.DeactivateCalls);
        Assert.True(application.Running);
        Assert.Contains(
            notifications,
            notification => notification.Title == "Health-check recovery blocked"
        );
        Assert.DoesNotContain(
            notifications,
            notification => notification.Title == "Resource restarted"
        );
    }

    /// <summary>Creates a health-aware application with one immediately failing, restart-enabled check.</summary>
    /// <param name="application">The fake application lifecycle controlled by the test.</param>
    /// <returns>A wrapper using deterministic process discovery.</returns>
    private static HealthCheckedApplication CreateWrapper(
        FakeApplicationLifecycle application)
    {
        var healthCheck = new ManagedHealthCheck(
            new HealthCheckConfig
            {
                Name = "Listener",
                Type = HealthCheckType.Listener,
                Protocol = ListenerProtocol.Tcp,
                Port = 12345,
                IntervalSeconds = 1,
                TimeoutSeconds = 5,
                FailureThreshold = 1,
                StartupDelaySeconds = 0,
                RestartOnFailure = true,
                Notifications = new NotificationConfig
                {
                    Target = [NotificationTarget.Popup]
                }
            },
            new FailingProbe(),
            new AlwaysActiveCondition()
        );

        return new HealthCheckedApplication(
            application,
            [healthCheck],
            () => application.Running
                ? new HashSet<int> { 42 }
                : new HashSet<int>()
        );
    }

    /// <summary>Advances an immediate failed probe through its start and completion ticks.</summary>
    /// <param name="wrapper">The health-aware application to advance.</param>
    private static void ConfirmHealthFailure(HealthCheckedApplication wrapper)
    {
        wrapper.Supervise();
        wrapper.Supervise();
    }

    /// <summary>Always returns one immediate unhealthy probe result.</summary>
    private sealed class FailingProbe : IHealthProbe
    {
        /// <summary>Returns a deterministic failed health result.</summary>
        /// <param name="ownerProcessIds">Unused fake process identifiers.</param>
        /// <param name="cancellationToken">Unused cancellation token.</param>
        /// <returns>An immediate unhealthy result.</returns>
        public Task<HealthProbeResult> CheckAsync(
            IReadOnlySet<int> ownerProcessIds,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(HealthProbeResult.Failure("Unhealthy."));
        }

        /// <summary>Clears no state because the probe is stateless.</summary>
        public void Reset()
        {
        }

        /// <summary>Releases no resources because the probe is stateless.</summary>
        public void Dispose()
        {
        }
    }

    /// <summary>Provides deterministic application lifecycle state without launching an operating-system process.</summary>
    private sealed class FakeApplicationLifecycle : IManagedApplicationLifecycle
    {
        /// <summary>Gets a valid-looking helper configuration for identity and notifications.</summary>
        public ManagedApplicationConfig Config { get; } = new()
        {
            Path = @"C:\Test\Helper.exe",
            Notifications = new NotificationConfig
            {
                Target = [NotificationTarget.Popup]
            }
        };


        /// <summary>Gets or sets whether another active profile blocks deactivation.</summary>
        public bool BlockDeactivation { get; set; }
        /// <summary>Gets or sets whether process discovery reports the helper as running.</summary>
        public bool Running { get; set; }

        /// <summary>Gets or sets whether activation makes the helper immediately discoverable.</summary>
        public bool CompleteLaunchImmediately { get; set; }

        /// <summary>Gets whether a simulated graceful close still requires supervision.</summary>
        public bool CloseOperationPending { get; private set; }

        /// <summary>Gets the number of activation requests received.</summary>
        public int ActivateCalls { get; private set; }

        /// <summary>Gets the number of deactivation requests received.</summary>
        public int DeactivateCalls { get; private set; }

        /// <summary>Gets the number of pending-close supervision ticks received.</summary>
        public int SuperviseDeactivationCalls { get; private set; }

        /// <summary>Gets the number of monitoring-suspension requests received.</summary>
        public int SuspendCalls { get; private set; }

        public event Action<IManagedResource, string>? ErrorOccurred
        {
            add { }
            remove { }
        }

        /// <summary>Gets the fake helper display name.</summary>
        public string DisplayName => "Helper.exe";

        /// <summary>Gets the helper-level notification targets.</summary>
        public IReadOnlyList<NotificationTarget> NotificationTargets =>
            Config.Notifications.Target;

        /// <summary>Reports the test-controlled running state.</summary>
        /// <returns>The current value of <see cref="Running"/>.</returns>
        public bool IsRunning() => Running;

        /// <summary>Records a launch request and optionally makes the process immediately discoverable.</summary>
        public void Activate()
        {
            ActivateCalls++;

            if (CompleteLaunchImmediately)
                Running = true;
        }

        /// <summary>Returns no ordinary lifecycle update.</summary>
        /// <returns><see cref="ManagedResourceUpdate.None"/>.</returns>
        public ManagedResourceUpdate Supervise() => ManagedResourceUpdate.None;

        /// <summary>Cancels the simulated close operation.</summary>
        public void CancelPendingRecovery() => CloseOperationPending = false;

        /// <summary>Records one monitoring-suspension request.</summary>
        public void SuspendMonitoring() => SuspendCalls++;

        /// <summary>Records a graceful close request.</summary>
        public void Deactivate()
        {
            DeactivateCalls++;
            if (BlockDeactivation)
                return;

            CloseOperationPending = true;
        }

        /// <summary>Finalizes the pending close after the fake process is confirmed absent.</summary>
        public void SuperviseDeactivation()
        {
            SuperviseDeactivationCalls++;

            if (!Running)
                CloseOperationPending = false;
        }

        /// <summary>Releases no resources because the fake owns none.</summary>
        public void Dispose()
        {
        }
    }
}
