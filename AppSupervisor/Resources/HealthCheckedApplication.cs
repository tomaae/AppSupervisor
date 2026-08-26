using AppSupervisor.Core;
using AppSupervisor.Health;
using AppSupervisor.Notifications;

namespace AppSupervisor.Resources;

/// <summary>
/// Decorates a managed application with independently targeted health checks and graceful recovery.
/// </summary>
public sealed class HealthCheckedApplication :
    IManagedResource,
    IResourceNotificationSource,
    IManagedResourceReadiness,
    IManagedResourceDeactivationState,
    IManagedResourceLifecycleWork,
    IPauseDrainWork,
    IRecoverableResourceErrorSource
{
    private readonly IManagedApplicationLifecycle _application;
    private readonly IReadOnlyList<ManagedHealthCheck> _healthChecks;
    private readonly Func<IReadOnlySet<int>> _processIdProvider;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<ManagedHealthCheck, AutomaticRecoveryBudget> _recoveryBudgets;

    private ManagedHealthCheck? _restartCheck;
    private bool _replacementStartRequested;
    private bool _restartRetryScheduled;
    private DateTime _restartRetryUtc;
    private bool _disposed;

    /// <summary>Creates a health-aware wrapper around one managed application.</summary>
    /// <param name="application">The application that owns process lifecycle and close fallback behavior.</param>
    /// <param name="healthChecks">The configured health checks associated with the application.</param>
    public HealthCheckedApplication(
        ManagedApplication application,
        IEnumerable<ManagedHealthCheck> healthChecks)
        : this(
            application,
            healthChecks,
            application.GetRunningProcessIds,
            SupervisorTime.Provider
        )
    {
    }

    /// <summary>Creates a health-aware wrapper with injectable application state and process discovery for lifecycle testing.</summary>
    /// <param name="application">The application lifecycle implementation to supervise.</param>
    /// <param name="healthChecks">The configured health checks associated with the application.</param>
    /// <param name="processIdProvider">Returns the currently matching helper process identifiers.</param>
    internal HealthCheckedApplication(
        IManagedApplicationLifecycle application,
        IEnumerable<ManagedHealthCheck> healthChecks,
        Func<IReadOnlySet<int>> processIdProvider,
        TimeProvider? timeProvider = null)
    {
        _application = application;
        _healthChecks = healthChecks.ToArray();
        _processIdProvider = processIdProvider;
        _timeProvider = timeProvider ?? SupervisorTime.Provider;
        _recoveryBudgets = _healthChecks.ToDictionary(
            healthCheck => healthCheck,
            _ => new AutomaticRecoveryBudget()
        );
        _application.ErrorOccurred += OnApplicationError;

        if (_application is IRecoverableResourceErrorSource recoverableSource)
            recoverableSource.ErrorCleared += OnApplicationErrorCleared;

        foreach (ManagedHealthCheck healthCheck in _healthChecks)
        {
            healthCheck.Failed += OnHealthCheckFailed;
            healthCheck.Recovered += OnHealthCheckRecovered;
        }
    }

    /// <summary>Occurs when ordinary application supervision fails outside health-check recovery.</summary>
    public event Action<IManagedResource, string>? ErrorOccurred;

    /// <summary>Occurs when the wrapped application lifecycle recovers from an ordinary error.</summary>
    public event Action<IManagedResource>? ErrorCleared;

    /// <summary>Occurs when a health check reports failure, recovery, restart success, or restart failure.</summary>
    public event Action<IManagedResource, ResourceNotification>? NotificationRequested;

    /// <summary>Gets the helper executable name displayed in notifications.</summary>
    public string DisplayName => _application.DisplayName;

    /// <summary>Gets the helper-level targets used for lifecycle notifications unrelated to a specific health check.</summary>
    public IReadOnlyList<NotificationTarget> NotificationTargets =>
        _application.NotificationTargets;

    /// <summary>Gets timer-owned health state for the read-only Supervisor API.</summary>
    internal IReadOnlyList<ManagedHealthCheck> ApiHealthChecks => _healthChecks;

    /// <summary>Gets the wrapped application for timer-cached API status.</summary>
    internal IManagedApplicationLifecycle ApiApplication => _application;

    /// <summary>Gets whether the wrapped application is still completing a close request.</summary>
    public bool DeactivationPending => _application.CloseOperationPending;

    /// <summary>Gets whether application lifecycle or health-restart reconciliation remains pending.</summary>
    bool IManagedResourceLifecycleWork.LifecycleWorkPending =>
        ((IManagedResourceLifecycleWork)_application).LifecycleWorkPending ||
        _restartCheck is not null;

    DateTime? IManagedResourceLifecycleWork.NextLifecycleDueUtc =>
        _restartRetryScheduled &&
        !((IManagedResourceLifecycleWork)_application).LifecycleWorkPending
            ? _restartRetryUtc
            : null;

    /// <summary>Gets whether a cancelled health probe is still unwinding.</summary>
    bool IPauseDrainWork.PauseDrainPending =>
        _healthChecks.Any(healthCheck => healthCheck.PauseDrainPending);

    /// <summary>Cancels health probes without interrupting application lifecycle transitions.</summary>
    void IPauseDrainWork.BeginPauseDrain()
    {
        ResetHealthChecks(clearErrors: true);
    }

    /// <summary>Reaps completed cancelled probes without starting replacements.</summary>
    void IPauseDrainWork.AdvancePauseDrain()
    {
        foreach (ManagedHealthCheck healthCheck in _healthChecks)
            healthCheck.AdvancePauseDrain();
    }

    /// <summary>Checks whether the wrapped helper process is started for dependency sequencing.</summary>
    /// <returns><see langword="true"/> when at least one matching helper process is running.</returns>
    public bool IsStarted() =>
        ((IManagedResourceReadiness)_application).IsStarted();

    /// <summary>Delegates one-time application initialization.</summary>
    public void Initialize() => ((IManagedResource)_application).Initialize();

    /// <summary>Ensures the helper is running and resets health-check sampling for the new active period.</summary>
    public void Activate()
    {
        if (_disposed)
            return;

        _restartCheck = null;
        _replacementStartRequested = false;
        _restartRetryScheduled = false;
        ResetRecoveryBudgets();
        ResetHealthChecks(clearErrors: true);
        _application.Activate();
    }

    /// <summary>Advances application lifecycle, health probes, and any requested graceful health recovery.</summary>
    /// <returns>A generic restart result only for ordinary application recovery.</returns>
    public ManagedResourceUpdate Supervise()
    {
        if (_disposed)
            return ManagedResourceUpdate.None;

        if (_restartCheck is not null)
        {
            BeginScheduledHealthRestart(UtcNow);
            return ManagedResourceUpdate.None;
        }

        ManagedResourceUpdate applicationUpdate = _application.Supervise();
        IReadOnlySet<int> processIds = _processIdProvider();

        if (processIds.Count == 0 ||
            (processIds.Count > 1 &&
                ProcessPathSnapshot.FindIndependentRootProcessIds(processIds).Count != 1))
        {
            ResetHealthChecks(clearErrors: true);
            return applicationUpdate;
        }

        if (applicationUpdate == ManagedResourceUpdate.Restarted)
            ResetHealthChecks(clearErrors: false);

        DateTime nowUtc = UtcNow;

        foreach (ManagedHealthCheck healthCheck in _healthChecks)
            healthCheck.Poll(processIds, nowUtc);

        return applicationUpdate;
    }

    /// <summary>Cancels application recovery and all health work as soon as the monitoring trigger disappears.</summary>
    public void CancelPendingRecovery()
    {
        SupervisorLog.WriteTrace(
            $"Helper '{DisplayName}': cancelling health and application recovery."
        );
        _restartCheck = null;
        _replacementStartRequested = false;
        _restartRetryScheduled = false;
        ResetRecoveryBudgets();
        ResetHealthChecks(clearErrors: true);
        _application.CancelPendingRecovery();
        SupervisorLog.WriteTrace(
            $"Helper '{DisplayName}': health and application recovery cancelled."
        );
    }

    /// <summary>Stops health work and begins the application's normal all-instance graceful deactivation.</summary>
    public void Deactivate()
    {
        _restartCheck = null;
        _replacementStartRequested = false;
        _restartRetryScheduled = false;
        ResetRecoveryBudgets();
        ResetHealthChecks(clearErrors: true);
        _application.Deactivate();
    }

    /// <summary>Advances the application's normal graceful profile-deactivation close operation.</summary>
    public void SuperviseDeactivation() => _application.SuperviseDeactivation();

    /// <summary>Advances the wrapped transition and completes health recovery after exact-path confirmation.</summary>
    ManagedResourceUpdate IManagedResourceLifecycleWork.AdvanceLifecycle(DateTime nowUtc)
    {
        BeginScheduledHealthRestart(nowUtc);
        ManagedResourceUpdate update =
            ((IManagedResourceLifecycleWork)_application).AdvanceLifecycle(nowUtc);

        if (update == ManagedResourceUpdate.Restarted)
            ResetHealthChecks(clearErrors: false);

        if (_restartCheck is not null)
            AdvanceHealthRestart(nowUtc);

        return update;
    }

    /// <summary>Cancels probes and health recovery while leaving the external helper untouched.</summary>
    public void SuspendMonitoring()
    {
        if (_restartCheck is not null)
        {
            _restartCheck = null;
            _replacementStartRequested = false;
            _restartRetryScheduled = false;
            _application.CancelPendingRecovery();
        }

        _application.SuspendMonitoring();
        ResetHealthChecks(clearErrors: true);
    }

    /// <summary>Unsubscribes events and disposes checks and the wrapped application without altering external processes.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _application.ErrorOccurred -= OnApplicationError;

        if (_application is IRecoverableResourceErrorSource recoverableSource)
            recoverableSource.ErrorCleared -= OnApplicationErrorCleared;

        foreach (ManagedHealthCheck healthCheck in _healthChecks)
        {
            healthCheck.Failed -= OnHealthCheckFailed;
            healthCheck.Recovered -= OnHealthCheckRecovered;
            healthCheck.Dispose();
        }

        _application.Dispose();
        ErrorOccurred = null;
        NotificationRequested = null;
        ErrorCleared = null;
    }

    /// <summary>Continues closing all helper instances, then starts exactly one replacement after confirming they are gone.</summary>
    /// <returns>No generic restart result because the wrapper publishes the check-specific warning itself.</returns>
    private void AdvanceHealthRestart(DateTime nowUtc)
    {
        ManagedHealthCheck recoveryCheck = _restartCheck!;

        if (_restartRetryScheduled)
            return;

        if (!_replacementStartRequested)
        {
            if (_restartCheck is null || _application.CloseOperationPending)
                return;

            _replacementStartRequested = true;
            _application.Activate();

            if (_restartCheck is null)
                return;
        }

        ((IManagedResourceLifecycleWork)_application).AdvanceLifecycle(nowUtc);

        if (_restartCheck is null ||
            ((IManagedResourceLifecycleWork)_application).LifecycleWorkPending)
            return;

        if (!_application.IsRunning())
            return;

        _restartCheck = null;
        _replacementStartRequested = false;
        _restartRetryScheduled = false;
        ResetHealthChecks(clearErrors: false);
        recoveryCheck.RearmAfterRecoveryAttempt(
            nowUtc,
            AutomaticRecoveryBudget.RetryDelay
        );
        AutomaticRecoveryBudget budget = _recoveryBudgets[recoveryCheck];
        PublishHealthNotification(
            recoveryCheck,
            NotificationSeverity.Warning,
            "Resource restarted",
            $"{DisplayName} was restarted because health check '{recoveryCheck.Name}' failed " +
            $"(automatic recovery attempt {budget.Attempts} of " +
            $"{AutomaticRecoveryBudget.MaximumAttempts}). A successful health probe is required " +
            "to reset the retry count.",
            ResourceErrorState.None
        );
    }

    /// <summary>Publishes a debounced health failure and requests one graceful restart when configured.</summary>
    /// <param name="healthCheck">The check that crossed its failure threshold.</param>
    /// <param name="detail">The probe's diagnostic detail.</param>
    private void OnHealthCheckFailed(ManagedHealthCheck healthCheck, string detail)
    {
        PublishHealthNotification(
            healthCheck,
            NotificationSeverity.Error,
            "Health check failed",
            $"{DisplayName} - {healthCheck.Name}\n{detail}",
            ResourceErrorState.Set
        );

        if (!healthCheck.RestartOnFailure || _restartCheck is not null)
            return;

        AutomaticRecoveryBudget budget = _recoveryBudgets[healthCheck];
        if (!budget.TryBeginAttempt(UtcNow))
        {
            PublishHealthNotification(
                healthCheck,
                NotificationSeverity.Error,
                "Health-check recovery limit reached",
                $"{DisplayName} - {healthCheck.Name}\nAutomatic recovery stopped after " +
                $"{budget.Attempts} of {AutomaticRecoveryBudget.MaximumAttempts} attempts. " +
                "A successful health probe or a new profile lifecycle is required to reset the limit.",
                ResourceErrorState.Set
            );
            return;
        }

        StartHealthRestart(healthCheck);
    }

    /// <summary>Publishes confirmed recovery and clears the check's active tray error state.</summary>
    /// <param name="healthCheck">The check whose first successful probe recovered it or that became inapplicable.</param>
    /// <param name="detail">The recovery or deactivation detail.</param>
    private void OnHealthCheckRecovered(ManagedHealthCheck healthCheck, string detail)
    {
        _recoveryBudgets[healthCheck].Reset();
        PublishHealthNotification(
            healthCheck,
            NotificationSeverity.Information,
            "Health check recovered",
            $"{DisplayName} - {healthCheck.Name}\n{detail}",
            ResourceErrorState.Clear
        );
    }

    /// <summary>Routes wrapped lifecycle errors through check-specific targets during health recovery.</summary>
    /// <param name="resource">The wrapped application.</param>
    /// <param name="message">The lifecycle failure detail.</param>
    private void OnApplicationError(IManagedResource resource, string message)
    {
        if (_restartCheck is null)
        {
            ErrorOccurred?.Invoke(this, message);
            return;
        }

        ManagedHealthCheck failedRecoveryCheck = _restartCheck;
        AutomaticRecoveryBudget budget = _recoveryBudgets[failedRecoveryCheck];
        budget.RecordFailure(UtcNow);
        _application.CancelPendingRecovery();
        _replacementStartRequested = false;
        _restartRetryScheduled = !budget.Exhausted;
        _restartRetryUtc = budget.NextAttemptUtc;

        if (budget.Exhausted)
            _restartCheck = null;

        PublishHealthNotification(
            failedRecoveryCheck,
            NotificationSeverity.Error,
            "Health-check recovery failed",
            $"{DisplayName} - {failedRecoveryCheck.Name}\n" +
            budget.DescribeFailure(message),
            ResourceErrorState.Set
        );
    }

    /// <summary>Forwards recovery of an ordinary wrapped application lifecycle error.</summary>
    /// <param name="resource">The wrapped application that recovered.</param>
    private void OnApplicationErrorCleared(IManagedResource resource)
    {
        ErrorCleared?.Invoke(this);
    }

    /// <summary>Raises one check-specific resource notification with a stable error-state key.</summary>
    /// <param name="healthCheck">The check that owns the notification targets and key.</param>
    /// <param name="severity">The notification severity.</param>
    /// <param name="title">The notification title.</param>
    /// <param name="message">The notification detail.</param>
    /// <param name="errorState">The recoverable tray error-state transition.</param>
    private void PublishHealthNotification(
        ManagedHealthCheck healthCheck,
        NotificationSeverity severity,
        string title,
        string message,
        ResourceErrorState errorState)
    {
        NotificationRequested?.Invoke(
            this,
            new ResourceNotification(
                healthCheck.Name,
                severity,
                title,
                message,
                healthCheck.NotificationTargets,
                errorState
            )
        );
    }

    /// <summary>Cancels and resets every check, optionally clearing active error-state notifications.</summary>
    /// <param name="clearErrors">Whether inapplicable checks clear their active tray error state.</param>
    private void ResetHealthChecks(bool clearErrors)
    {
        foreach (ManagedHealthCheck healthCheck in _healthChecks)
            healthCheck.Suspend(clearErrors);
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    private void StartHealthRestart(ManagedHealthCheck healthCheck)
    {
        _restartCheck = healthCheck;
        _replacementStartRequested = false;
        _restartRetryScheduled = false;
        _application.Deactivate();

        if (!ReferenceEquals(_restartCheck, healthCheck) || _restartRetryScheduled)
            return;

        if (!_application.CloseOperationPending && _application.IsRunning())
        {
            AutomaticRecoveryBudget budget = _recoveryBudgets[healthCheck];
            budget.RecordFailure(UtcNow);
            _restartCheck = null;
            PublishHealthNotification(
                healthCheck,
                NotificationSeverity.Error,
                "Health-check recovery blocked",
                $"{DisplayName} - {healthCheck.Name}\nAutomatic recovery attempt " +
                $"{budget.Attempts} of {AutomaticRecoveryBudget.MaximumAttempts} was blocked. " +
                "The helper is still required by another active profile, so AppSupervisor left it running.",
                ResourceErrorState.Set
            );
        }
    }

    private void BeginScheduledHealthRestart(DateTime nowUtc)
    {
        if (!_restartRetryScheduled || _restartCheck is not ManagedHealthCheck healthCheck ||
            nowUtc < _restartRetryUtc)
        {
            return;
        }

        AutomaticRecoveryBudget budget = _recoveryBudgets[healthCheck];
        if (!budget.TryBeginAttempt(nowUtc))
            return;

        StartHealthRestart(healthCheck);
    }

    private void ResetRecoveryBudgets()
    {
        foreach (AutomaticRecoveryBudget budget in _recoveryBudgets.Values)
            budget.Reset();
    }

}
