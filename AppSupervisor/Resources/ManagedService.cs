using AppSupervisor.Core;
using AppSupervisor.Notifications;
using AppSupervisor.ServiceControl;

namespace AppSupervisor.Resources;

/// <summary>
/// Supervises one Windows service by internal service name using the shared supervisor-profile tick.
/// </summary>
public sealed class ManagedService :
    IManagedResource,
    IManagedResourceReadiness,
    IManagedResourceDeactivationState,
    IManagedResourceLifecycleWork,
    IRecoverableResourceErrorSource
{
    private const int OperationTimeoutSeconds = 30;

    private readonly TimeSpan _restartTimeout;
    private readonly Func<string, IWindowsServiceController> _controllerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly AutomaticRecoveryBudget _recoveryBudget = new();

    private IWindowsServiceController? _controller;
    private DateTime? _missingSince;
    private DateTime? _pendingOperationSince;
    private DateTime? _stopStartedUtc;
    private bool _available;
    private bool _activeDemand;
    private bool _disposed;
    private bool _startAfterStop;
    private bool _startAwaitingConfirmation;
    private bool _continueAwaitingConfirmation;
    private bool _stopCommandSent;
    private bool _stopPending;
    private Task? _stopCommandTask;
    private bool _statusErrorReported;
    private bool _errorActive;
    private bool _hasObservedState;
    private ServiceRuntimeState? _lastObservedState;
    private ServiceErrorRecovery _errorRecovery;

    /// <summary>
    /// Creates a managed service that will connect to the native Service Control Manager during initialization.
    /// </summary>
    /// <param name="config">The service identity and restart behavior.</param>
    /// <param name="restartTimeout">How long an unexpectedly stopped service may remain stopped before restart.</param>
    public ManagedService(
        ManagedServiceConfig config,
        TimeSpan restartTimeout)
        : this(
            config,
            restartTimeout,
            serviceName => new WindowsServiceController(serviceName))
    {
    }

    /// <summary>
    /// Creates a managed service with an injectable controller factory for isolated lifecycle testing.
    /// </summary>
    /// <param name="config">The service identity and restart behavior.</param>
    /// <param name="restartTimeout">How long an unexpectedly stopped service may remain stopped before restart.</param>
    /// <param name="controllerFactory">Creates the platform-specific controller for the configured service.</param>
    internal ManagedService(
        ManagedServiceConfig config,
        TimeSpan restartTimeout,
        Func<string, IWindowsServiceController> controllerFactory,
        TimeProvider? timeProvider = null)
    {
        Config = config;
        _restartTimeout = restartTimeout;
        _controllerFactory = controllerFactory;
        _timeProvider = timeProvider ?? SupervisorTime.Provider;
    }

    /// <summary>Occurs when the service cannot complete a requested lifecycle operation.</summary>
    public event Action<IManagedResource, string>? ErrorOccurred;

    /// <summary>Occurs when Windows service communication succeeds after an error.</summary>
    public event Action<IManagedResource>? ErrorCleared;

    /// <summary>Gets the service identity, recovery, and notification settings.</summary>
    public ManagedServiceConfig Config { get; }

    /// <summary>Gets the internal Windows service name used in notifications.</summary>
    public string DisplayName => Config.ServiceName;

    /// <summary>
    /// Gets the presentation targets configured specifically for this helper service.
    /// </summary>
    public IReadOnlyList<NotificationTarget> NotificationTargets => Config.Notifications.Target;

    /// <summary>Gets whether Windows is still processing the requested service stop.</summary>
    public bool DeactivationPending => _stopPending;

    /// <summary>Gets the latest observed or predicted service state without querying Windows.</summary>
    internal ConfigurationResourceRuntimeStatus CachedRuntimeStatus =>
        _stopPending
            ? ConfigurationResourceRuntimeStatus.Stopping
            : (!_hasObservedState || _lastObservedState is null
                ? ConfigurationResourceRuntimeStatus.Unknown
                : _lastObservedState switch
                {
                    ServiceRuntimeState.StartPending or ServiceRuntimeState.ContinuePending =>
                        ConfigurationResourceRuntimeStatus.Starting,
                    ServiceRuntimeState.StopPending or ServiceRuntimeState.PausePending =>
                        ConfigurationResourceRuntimeStatus.Stopping,
                    ServiceRuntimeState.Running => ConfigurationResourceRuntimeStatus.Running,
                    ServiceRuntimeState.Stopped or ServiceRuntimeState.Paused =>
                        ConfigurationResourceRuntimeStatus.NotRunning,
                    _ => ConfigurationResourceRuntimeStatus.Unknown
                });

    /// <summary>Gets whether Windows is still processing an accepted start, continue, or stop command.</summary>
    bool IManagedResourceLifecycleWork.LifecycleWorkPending =>
        _stopPending || _pendingOperationSince is not null;

    /// <summary>Checks whether the service has reached the running state for dependency sequencing.</summary>
    /// <returns><see langword="true"/> when Windows reports the service as running.</returns>
    public bool IsStarted()
    {
        if (!_available || _disposed)
            return false;

        return _hasObservedState
            ? _lastObservedState == ServiceRuntimeState.Running
            : TryGetState() == ServiceRuntimeState.Running;
    }

    /// <summary>
    /// Opens the service with all required rights and enforces Manual startup once for this runtime instance.
    /// </summary>
    public void Initialize()
    {
        if (_disposed)
            return;

        _controller?.Dispose();
        _controller = null;
        _available = false;
        _hasObservedState = false;
        _lastObservedState = null;

        try
        {
            _controller = _controllerFactory(Config.ServiceName);
            _controller.EnsureManualStartAndRequiredAccess();
            _available = true;
            _statusErrorReported = false;
            ClearError();
        }
        catch (Exception ex)
        {
            _controller?.Dispose();
            _controller = null;
            ReportError(
                ServiceErrorRecovery.Never,
                $"Service initialization failed. AppSupervisor could not verify start/stop permissions " +
                $"or enforce Manual startup. {ex.Message}"
            );
        }
    }

    /// <summary>
    /// Ensures the service is running when its supervisor profile becomes active.
    /// </summary>
    public void Activate()
    {
        if (!_available || _disposed)
            return;

        if (!_activeDemand)
            _recoveryBudget.Reset();

        _activeDemand = true;
        _missingSince = null;

        if (_stopPending)
        {
            _startAfterStop = true;
            return;
        }

        ServiceRuntimeState? state = TryGetState();

        if (state is null)
            return;

        switch (state.Value)
        {
            case ServiceRuntimeState.Running:
                ConfirmRunning();
                ClearPendingOperation();
                break;

            case ServiceRuntimeState.Stopped:
                TryStart();
                break;

            case ServiceRuntimeState.Paused:
                TryContinue();
                break;

            case ServiceRuntimeState.StopPending:
                BeginObservedStop(restartAfterStop: true);
                break;

            case ServiceRuntimeState.StartPending:
            case ServiceRuntimeState.ContinuePending:
            case ServiceRuntimeState.PausePending:
                TrackPendingOperation();
                break;
        }
    }

    /// <summary>
    /// Monitors service state, waits through transient states, and restarts an unexpectedly stopped service after timeout.
    /// </summary>
    /// <returns><see cref="ManagedResourceUpdate.Restarted"/> when this cycle sends a successful replacement start request.</returns>
    public ManagedResourceUpdate Supervise()
    {
        if (!_available || _disposed)
            return ManagedResourceUpdate.None;

        if (_stopPending)
            return AdvanceStop(profileActive: true);

        ServiceRuntimeState? state = TryGetState();

        if (state is null)
            return ManagedResourceUpdate.None;

        switch (state.Value)
        {
            case ServiceRuntimeState.Running:
                _missingSince = null;
                ConfirmRunning();
                ClearPendingOperation();
                return ManagedResourceUpdate.None;

            case ServiceRuntimeState.Stopped:
                RecordUnconfirmedStartFailure();
                ClearPendingOperation();
                return SuperviseStoppedService();

            case ServiceRuntimeState.Paused:
                RecordUnconfirmedContinueFailure();
                ClearPendingOperation();
                TryContinue();
                return ManagedResourceUpdate.None;

            case ServiceRuntimeState.StopPending:
                BeginObservedStop(restartAfterStop: Config.Restart);
                return ManagedResourceUpdate.None;

            case ServiceRuntimeState.StartPending:
            case ServiceRuntimeState.ContinuePending:
            case ServiceRuntimeState.PausePending:
                CheckPendingOperationTimeout(state.Value);
                return ManagedResourceUpdate.None;

            default:
                return ManagedResourceUpdate.None;
        }
    }

    /// <summary>
    /// Cancels pending restart state immediately when the monitoring trigger disappears.
    /// </summary>
    public void CancelPendingRecovery()
    {
        _activeDemand = false;
        _missingSince = null;
        _startAfterStop = false;
        _recoveryBudget.Reset();
    }

    /// <summary>
    /// Resets restart and pending-state timing while preserving any service command already accepted by Windows.
    /// </summary>
    public void SuspendMonitoring()
    {
        _missingSince = null;
        ClearPendingOperation();
    }

    /// <summary>
    /// Begins a graceful Service Control Manager stop request after the profile close timeout period expires.
    /// </summary>
    public void Deactivate()
    {
        if (!_available || _disposed)
            return;

        _activeDemand = false;
        _missingSince = null;
        _startAfterStop = false;
        _recoveryBudget.Reset();

        ServiceRuntimeState? state = TryGetState();

        if (state is null)
            return;

        if (state == ServiceRuntimeState.Stopped)
        {
            ClearStopState();
            return;
        }

        _stopPending = true;
        _stopStartedUtc = UtcNow;
        _stopCommandSent = state == ServiceRuntimeState.StopPending;

        if (state is ServiceRuntimeState.Running or ServiceRuntimeState.Paused)
            TrySendStop();
    }

    /// <summary>
    /// Confirms that a requested service stop completes and reports a non-destructive timeout failure.
    /// </summary>
    public void SuperviseDeactivation()
    {
        if (!_available || _disposed || !_stopPending)
            return;

        AdvanceStop(profileActive: false);
    }

    /// <summary>Advances accepted service-control operations from the shared lifecycle timer.</summary>
    ManagedResourceUpdate IManagedResourceLifecycleWork.AdvanceLifecycle(DateTime nowUtc)
    {
        if (!_available || _disposed)
            return ManagedResourceUpdate.None;

        if (_stopPending)
            return AdvanceStop(profileActive: _activeDemand);

        if (_pendingOperationSince is null)
            return ManagedResourceUpdate.None;

        return Supervise();
    }

    /// <summary>
    /// Releases the service controller and removes event subscribers without changing service state.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        IWindowsServiceController? controller = _controller;
        Task? stopCommandTask = _stopCommandTask;
        _controller = null;
        _stopCommandTask = null;

        if (stopCommandTask is { IsCompleted: false })
        {
            _ = stopCommandTask.ContinueWith(
                completedTask =>
                {
                    _ = completedTask.Exception;
                    controller?.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
        }
        else
        {
            _ = stopCommandTask?.Exception;
            controller?.Dispose();
        }

        ErrorOccurred = null;
        ErrorCleared = null;
    }

    /// <summary>
    /// Applies restart timeout behavior after the service is confirmed stopped during active supervision.
    /// </summary>
    /// <returns><see cref="ManagedResourceUpdate.Restarted"/> when a restart request is sent successfully.</returns>
    private ManagedResourceUpdate SuperviseStoppedService()
    {
        if (!Config.Restart)
            return ManagedResourceUpdate.None;

        if (_missingSince is null)
        {
            _missingSince = UtcNow;
            return ManagedResourceUpdate.None;
        }

        if (_recoveryBudget.Exhausted ||
            UtcNow - _missingSince < _restartTimeout ||
            UtcNow < _recoveryBudget.NextAttemptUtc)
            return ManagedResourceUpdate.None;

        _missingSince = UtcNow;

        return TryStart()
            ? ManagedResourceUpdate.Restarted
            : ManagedResourceUpdate.None;
    }

    /// <summary>
    /// Advances a pending stop and optionally starts the service again when an active profile returned during shutdown.
    /// </summary>
    /// <param name="profileActive">Whether starting after the confirmed stop is currently allowed.</param>
    /// <returns><see cref="ManagedResourceUpdate.Restarted"/> when a replacement start request is sent.</returns>
    private ManagedResourceUpdate AdvanceStop(bool profileActive)
    {
        if (!FinishStopCommandIfReady())
            return ManagedResourceUpdate.None;

        ServiceRuntimeState? state = TryGetState();

        if (state is null)
            return ManagedResourceUpdate.None;

        if (state == ServiceRuntimeState.Stopped)
        {
            bool shouldStart = profileActive && _startAfterStop;
            ClearStopState();

            return shouldStart && TryStart()
                ? ManagedResourceUpdate.Restarted
                : ManagedResourceUpdate.None;
        }

        if (!_stopCommandSent &&
            state is ServiceRuntimeState.Running or ServiceRuntimeState.Paused)
        {
            TrySendStop();
        }

        if (_stopStartedUtc is not null &&
            UtcNow - _stopStartedUtc >= TimeSpan.FromSeconds(OperationTimeoutSeconds))
        {
            ReportError(
                ServiceErrorRecovery.Stopped,
                $"Could not stop service '{Config.ServiceName}' within {OperationTimeoutSeconds} seconds. " +
                "No process termination was attempted."
            );
            ClearStopState();
        }

        return ManagedResourceUpdate.None;
    }

    /// <summary>
    /// Records a stop already initiated outside AppSupervisor and waits to restart only if the profile remains active.
    /// </summary>
    /// <param name="restartAfterStop">Whether to start the service after the observed stop completes.</param>
    private void BeginObservedStop(bool restartAfterStop)
    {
        _stopPending = true;
        _stopCommandSent = true;
        _startAfterStop = restartAfterStop;
        _stopStartedUtc ??= UtcNow;
    }

    /// <summary>
    /// Sends a service start request and converts any failure into a supervision error.
    /// </summary>
    /// <returns><see langword="true"/> when Windows accepts the start request.</returns>
    private bool TryStart()
    {
        DateTime nowUtc = UtcNow;

        if (!_recoveryBudget.TryBeginAttempt(nowUtc))
            return false;

        try
        {
            _controller!.Start();
            _missingSince = null;
            _pendingOperationSince = nowUtc;
            _startAwaitingConfirmation = true;
            _continueAwaitingConfirmation = false;
            RememberState(ServiceRuntimeState.StartPending);
            return true;
        }
        catch (Exception ex)
        {
            _recoveryBudget.RecordFailure(nowUtc);
            ReportError(
                ServiceErrorRecovery.Running,
                _recoveryBudget.DescribeFailure(
                    $"Could not start service '{Config.ServiceName}'. {ex.Message}"
                )
            );
            return false;
        }
    }

    /// <summary>
    /// Sends a service stop request and converts any failure into a supervision error.
    /// </summary>
    private void TrySendStop()
    {
        try
        {
            IWindowsServiceController controller = _controller!;
            _stopCommandTask = Task.Factory.StartNew(
                controller.Stop,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
        }
        catch (Exception ex)
        {
            ReportError(ServiceErrorRecovery.Stopped, $"Could not stop service '{Config.ServiceName}'. {ex.Message}");
            ClearStopState();
        }
    }

    /// <summary>Consumes a completed stop request without letting its native wait block supervision.</summary>
    /// <returns>
    /// <see langword="true"/> when state polling may continue; otherwise the request is still
    /// running or failed and this lifecycle pass is complete.
    /// </returns>
    private bool FinishStopCommandIfReady()
    {
        if (_stopCommandTask is null)
            return true;

        if (!_stopCommandTask.IsCompleted)
            return false;

        Task completedTask = _stopCommandTask;
        _stopCommandTask = null;

        try
        {
            completedTask.GetAwaiter().GetResult();
            _stopCommandSent = true;
            RememberState(ServiceRuntimeState.StopPending);
            return true;
        }
        catch (Exception ex)
        {
            ReportError(
                ServiceErrorRecovery.Stopped,
                $"Could not stop service '{Config.ServiceName}'. {ex.Message}"
            );
            ClearStopState();
            return false;
        }
    }

    /// <summary>
    /// Sends Continue to a paused service and reports services that reject continuation.
    /// </summary>
    private void TryContinue()
    {
        DateTime nowUtc = UtcNow;

        if (!_recoveryBudget.TryBeginAttempt(nowUtc))
            return;

        try
        {
            _controller!.Continue();
            _pendingOperationSince = nowUtc;
            _continueAwaitingConfirmation = true;
            _startAwaitingConfirmation = false;
            RememberState(ServiceRuntimeState.ContinuePending);
        }
        catch (Exception ex)
        {
            _recoveryBudget.RecordFailure(nowUtc);
            ReportError(
                ServiceErrorRecovery.Running,
                _recoveryBudget.DescribeFailure(
                    $"Could not continue paused service '{Config.ServiceName}'. {ex.Message}"
                )
            );
        }
    }

    /// <summary>
    /// Reads service state while suppressing repeated identical access failures on every supervisor tick.
    /// </summary>
    /// <returns>The current state, or <see langword="null"/> when querying fails.</returns>
    internal ServiceRuntimeState? TryGetState()
    {
        if (!_available || _disposed)
            return null;

        if (_stopCommandTask is { IsCompleted: false })
            return _lastObservedState;

        try
        {
            ServiceRuntimeState state = _controller!.GetState();
            RememberState(state);
            _statusErrorReported = false;
            ClearErrorIfRecovered(state);
            return state;
        }
        catch (Exception ex)
        {
            _hasObservedState = true;
            _lastObservedState = null;
            if (!_statusErrorReported)
            {
                _statusErrorReported = true;
                ReportError(ServiceErrorRecovery.AnySuccessfulQuery, $"Could not query service '{Config.ServiceName}'. {ex.Message}");
            }

            return null;
        }
    }

    /// <summary>Shares one observed or predicted service state with readiness checks in this pass.</summary>
    private void RememberState(ServiceRuntimeState state)
    {
        _hasObservedState = true;
        _lastObservedState = state;
    }

    /// <summary>
    /// Starts tracking how long Windows remains in a transitional service state.
    /// </summary>
    private void TrackPendingOperation()
    {
        _pendingOperationSince ??= UtcNow;
    }

    /// <summary>
    /// Reports a service that remains indefinitely in a pending state.
    /// </summary>
    /// <param name="state">The transitional state currently reported by Windows.</param>
    private void CheckPendingOperationTimeout(ServiceRuntimeState state)
    {
        TrackPendingOperation();

        if (UtcNow - _pendingOperationSince < TimeSpan.FromSeconds(OperationTimeoutSeconds))
            return;

        ReportError(
            state == ServiceRuntimeState.StopPending ? ServiceErrorRecovery.Stopped : ServiceErrorRecovery.Running,
            $"Service '{Config.ServiceName}' remained in {state} for more than {OperationTimeoutSeconds} seconds."
        );
        _pendingOperationSince = UtcNow;
    }

    /// <summary>
    /// Clears transitional start, continue, or pause timing state.
    /// </summary>
    private void ClearPendingOperation()
    {
        _pendingOperationSince = null;
    }

    /// <summary>
    /// Clears all state associated with a completed, failed, or cancelled stop operation.
    /// </summary>
    private void ClearStopState()
    {
        _stopPending = false;
        _stopCommandSent = false;
        _startAfterStop = false;
        _stopStartedUtc = null;
    }

    /// <summary>Confirms the target service state and clears every consecutive recovery attempt.</summary>
    private void ConfirmRunning()
    {
        _startAwaitingConfirmation = false;
        _continueAwaitingConfirmation = false;
        _recoveryBudget.Reset();
    }

    /// <summary>Turns an accepted start that returned to Stopped into one failed recovery attempt.</summary>
    private void RecordUnconfirmedStartFailure()
    {
        if (!_startAwaitingConfirmation)
            return;

        _startAwaitingConfirmation = false;
        _recoveryBudget.RecordFailure(UtcNow);
        ReportError(
            ServiceErrorRecovery.Running,
            _recoveryBudget.DescribeFailure(
                $"Service '{Config.ServiceName}' did not reach the Running state after Windows accepted its start request."
            )
        );
    }

    /// <summary>Turns an accepted continue that returned to Paused into one failed recovery attempt.</summary>
    private void RecordUnconfirmedContinueFailure()
    {
        if (!_continueAwaitingConfirmation)
            return;

        _continueAwaitingConfirmation = false;
        _recoveryBudget.RecordFailure(UtcNow);
        ReportError(
            ServiceErrorRecovery.Running,
            _recoveryBudget.DescribeFailure(
                $"Service '{Config.ServiceName}' remained paused after Windows accepted its continue request."
            )
        );
    }

    /// <summary>Gets suspend-aware time for service operation deadlines and recovery scheduling.</summary>
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Raises a user-visible service error without throwing from the shared timer tick.
    /// </summary>
    /// <param name="message">The error message forwarded to the supervisor profile.</param>
    /// <param name="recovery">The service state that proves this operation recovered.</param>
    private void ReportError(
        ServiceErrorRecovery recovery,
        string message)
    {
        _errorActive = true;
        _errorRecovery = recovery;
        ErrorOccurred?.Invoke(this, message);
    }

    /// <summary>Clears one active service error after Windows service communication succeeds.</summary>
    private void ClearError()
    {
        if (!_errorActive)
            return;

        _errorActive = false;
        ErrorCleared?.Invoke(this);
    }

    /// <summary>Clears the active error only after the failed operation reaches its intended state.</summary>
    /// <param name="state">The latest successfully queried service state.</param>
    private void ClearErrorIfRecovered(ServiceRuntimeState state)
    {
        bool recovered = _errorRecovery switch
        {
            ServiceErrorRecovery.AnySuccessfulQuery => true,
            ServiceErrorRecovery.Running => state == ServiceRuntimeState.Running,
            ServiceErrorRecovery.Stopped => state == ServiceRuntimeState.Stopped,
            _ => false
        };

        if (recovered)
            ClearError();
    }

    /// <summary>Defines the state that proves recovery from one reported service operation error.</summary>
    private enum ServiceErrorRecovery
    {
        Never,
        AnySuccessfulQuery,
        Running,
        Stopped
    }
}
