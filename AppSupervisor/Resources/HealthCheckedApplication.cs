using AppSupervisor.Core;
using AppSupervisor.Health;
using AppSupervisor.Notifications;

namespace AppSupervisor.Resources;

/// <summary>
/// Decorates a managed application with independently targeted health checks and graceful recovery.
/// </summary>
public sealed class HealthCheckedApplication : IManagedResource, IResourceNotificationSource, IManagedResourceReadiness, IRecoverableResourceErrorSource
{
    private readonly IManagedApplicationLifecycle _application;
    private readonly IReadOnlyList<ManagedHealthCheck> _healthChecks;
    private readonly Func<IReadOnlySet<int>> _processIdProvider;

    private ManagedHealthCheck? _restartCheck;
    private bool _replacementStartRequested;
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
            () => ProcessPathDiscovery.FindRunningProcessIds(application.Config.Path)
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
        Func<IReadOnlySet<int>> processIdProvider)
    {
        _application = application;
        _healthChecks = healthChecks.ToArray();
        _processIdProvider = processIdProvider;
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

    /// <summary>Checks whether the wrapped helper process is started for dependency sequencing.</summary>
    /// <returns><see langword="true"/> when at least one matching helper process is running.</returns>
    public bool IsStarted() => _application.IsRunning();

    /// <summary>Delegates one-time application initialization.</summary>
    public void Initialize() => ((IManagedResource)_application).Initialize();

    /// <summary>Ensures the helper is running and resets health-check sampling for the new active period.</summary>
    public void Activate()
    {
        if (_disposed)
            return;

        _restartCheck = null;
        _replacementStartRequested = false;
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
            return AdvanceHealthRestart();

        ManagedResourceUpdate applicationUpdate = _application.Supervise();
        IReadOnlySet<int> processIds = _processIdProvider();

        if (processIds.Count != 1)
        {
            ResetHealthChecks(clearErrors: true);
            return applicationUpdate;
        }

        if (applicationUpdate == ManagedResourceUpdate.Restarted)
            ResetHealthChecks(clearErrors: false);

        DateTime nowUtc = DateTime.UtcNow;

        foreach (ManagedHealthCheck healthCheck in _healthChecks)
            healthCheck.Poll(processIds, nowUtc);

        return applicationUpdate;
    }

    /// <summary>Cancels application recovery and all health work as soon as the monitoring trigger disappears.</summary>
    public void CancelPendingRecovery()
    {
        _restartCheck = null;
        _replacementStartRequested = false;
        ResetHealthChecks(clearErrors: true);
        _application.CancelPendingRecovery();
    }

    /// <summary>Stops health work and begins the application's normal all-instance graceful deactivation.</summary>
    public void Deactivate()
    {
        _restartCheck = null;
        _replacementStartRequested = false;
        ResetHealthChecks(clearErrors: true);
        _application.Deactivate();
    }

    /// <summary>Advances the application's normal graceful profile-deactivation close operation.</summary>
    public void SuperviseDeactivation() => _application.SuperviseDeactivation();

    /// <summary>Cancels probes and health recovery while leaving the external helper untouched.</summary>
    public void SuspendMonitoring()
    {
        if (_restartCheck is not null)
        {
            _restartCheck = null;
            _replacementStartRequested = false;
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
    private ManagedResourceUpdate AdvanceHealthRestart()
    {
        ManagedHealthCheck recoveryCheck = _restartCheck!;

        if (!_replacementStartRequested)
        {
            if (_application.CloseOperationPending)
                _application.SuperviseDeactivation();

            if (_restartCheck is null || _application.CloseOperationPending)
                return ManagedResourceUpdate.None;

            _replacementStartRequested = true;
            _application.Activate();

            if (_restartCheck is null)
                return ManagedResourceUpdate.None;
        }

        if (_application.CloseOperationPending)
        {
            _application.Supervise();

            if (_restartCheck is null || _application.CloseOperationPending)
                return ManagedResourceUpdate.None;
        }

        if (!_application.IsRunning())
            return ManagedResourceUpdate.None;

        _restartCheck = null;
        _replacementStartRequested = false;
        ResetHealthChecks(clearErrors: false);
        PublishHealthNotification(
            recoveryCheck,
            NotificationSeverity.Warning,
            "Resource restarted",
            $"{DisplayName} was restarted because health check '{recoveryCheck.Name}' failed.",
            ResourceErrorState.None
        );
        return ManagedResourceUpdate.None;
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

        _restartCheck = healthCheck;
        _replacementStartRequested = false;
        _application.Deactivate();

        if (!_application.CloseOperationPending && _application.IsRunning())
        {
            _restartCheck = null;
            PublishHealthNotification(
                healthCheck,
                NotificationSeverity.Error,
                "Health-check recovery blocked",
                $"{DisplayName} - {healthCheck.Name}\nThe helper is still required by another active profile, so AppSupervisor left it running.",
                ResourceErrorState.Set
            );
        }
    }

    /// <summary>Publishes confirmed recovery and clears the check's active tray error state.</summary>
    /// <param name="healthCheck">The check whose first successful probe recovered it or that became inapplicable.</param>
    /// <param name="detail">The recovery or deactivation detail.</param>
    private void OnHealthCheckRecovered(ManagedHealthCheck healthCheck, string detail)
    {
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
        _restartCheck = null;
        _replacementStartRequested = false;
        PublishHealthNotification(
            failedRecoveryCheck,
            NotificationSeverity.Error,
            "Health-check recovery failed",
            $"{DisplayName} - {failedRecoveryCheck.Name}\n{message}",
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

}
