using AppSupervisor.Core;
using AppSupervisor.Notifications;
using AppSupervisor.Obs;

namespace AppSupervisor.Resources;

/// <summary>
/// Queues a deterministic OBS action for profile activation and intentionally performs no inverse
/// action when the profile deactivates.
/// </summary>
internal sealed class ObsResource :
    IManagedResource,
    IManagedResourceReadiness,
    IManagedResourceDeactivationState,
    IManagedResourceLifecycleWork,
    IPauseDrainWork,
    IRecoverableResourceErrorSource
{
    private readonly object _stateLock = new();
    private readonly ObsResourceConfig _configuration;
    private readonly IObsWebSocketClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly AutomaticRecoveryBudget _recoveryBudget = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _operationCancellation;
    private bool _operationQueued;
    private bool _operationPending;
    private bool _profileActive;
    private bool _activationComplete;
    private bool _errorActive;
    private DateTime _nextAttemptUtc;
    private bool _disposed;

    internal ObsResource(
        ObsResourceConfig configuration,
        ObsIntegrationConfig integration)
        : this(configuration, new ObsWebSocketClient(integration), SupervisorTime.Provider)
    {
    }

    internal ObsResource(
        ObsResourceConfig configuration,
        IObsWebSocketClient client,
        TimeProvider? timeProvider = null)
    {
        _configuration = configuration;
        _client = client;
        _timeProvider = timeProvider ?? SupervisorTime.Provider;
    }

    public event Action<IManagedResource, string>? ErrorOccurred;

    public event Action<IManagedResource>? ErrorCleared;

    public string DisplayName => GetDisplayName(_configuration);

    public IReadOnlyList<NotificationTarget> NotificationTargets =>
        _configuration.Notifications.Target;

    public bool LifecycleWorkPending
    {
        get
        {
            lock (_stateLock)
                return !_disposed && (
                    _operationQueued ||
                    _operationPending ||
                    (_profileActive && !_activationComplete && !_recoveryBudget.Exhausted)
                );
        }
    }

    public DateTime? NextLifecycleDueUtc
    {
        get
        {
            lock (_stateLock)
            {
                return !_operationQueued && !_operationPending &&
                    _profileActive && !_activationComplete &&
                    !_recoveryBudget.Exhausted
                        ? _nextAttemptUtc
                        : null;
            }
        }
    }

    public bool DeactivationPending
    {
        get
        {
            lock (_stateLock)
                return !_profileActive && (_operationQueued || _operationPending);
        }
    }

    public bool PauseDrainPending
    {
        get
        {
            lock (_stateLock)
                return !_disposed && (_operationQueued || _operationPending);
        }
    }

    public void Activate()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;

            if (!_profileActive)
                _recoveryBudget.Reset();

            _profileActive = true;
            _activationComplete = false;

            if (!_operationQueued && !_operationPending)
                _operationQueued = true;
        }
    }

    public bool IsStarted()
    {
        lock (_stateLock)
            return !_disposed && _profileActive && _activationComplete;
    }

    public ManagedResourceUpdate Supervise()
    {
        lock (_stateLock)
        {
            if (_disposed || !_profileActive || _activationComplete ||
                _operationQueued || _operationPending)
            {
                return ManagedResourceUpdate.None;
            }

            if (!_recoveryBudget.Exhausted &&
                _timeProvider.GetUtcNow().UtcDateTime >= _nextAttemptUtc)
                _operationQueued = true;

            return ManagedResourceUpdate.None;
        }
    }

    public void CancelPendingRecovery()
    {
        // An activation action already accepted by the ordered startup sequence is allowed to drain.
    }

    public void SuspendMonitoring()
    {
        // OBS actions are not continuously monitored and accepted activation work is left intact.
    }

    /// <summary>Marks the profile inactive without sending any OBS request.</summary>
    public void Deactivate()
    {
        lock (_stateLock)
        {
            _profileActive = false;
            _activationComplete = false;
            _recoveryBudget.Reset();
        }
    }

    public void SuperviseDeactivation()
    {
    }

    public ManagedResourceUpdate AdvanceLifecycle(DateTime nowUtc)
    {
        lock (_stateLock)
        {
            if (_disposed || _operationPending)
                return ManagedResourceUpdate.None;

            if (!_operationQueued && _profileActive && !_activationComplete &&
                !_recoveryBudget.Exhausted && nowUtc >= _nextAttemptUtc)
            {
                _operationQueued = true;
            }

            if (!_operationQueued)
                return ManagedResourceUpdate.None;

            if (!_recoveryBudget.TryBeginAttempt(nowUtc))
            {
                if (_recoveryBudget.Exhausted)
                    _operationQueued = false;
                return ManagedResourceUpdate.None;
            }

            _operationQueued = false;
            _operationPending = true;
            _operationCancellation?.Dispose();
            _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token
            );
            _ = CompleteActionAsync(_operationCancellation.Token);
            return ManagedResourceUpdate.None;
        }
    }

    public void BeginPauseDrain()
    {
    }

    public void AdvancePauseDrain()
    {
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _operationQueued = false;
            _lifetimeCancellation.Cancel();
            _operationCancellation?.Cancel();
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            _operationPending = false;
            _client.Dispose();
            ErrorOccurred = null;
            ErrorCleared = null;
        }

        _lifetimeCancellation.Dispose();
    }

    internal static string GetDisplayName(ObsResourceConfig configuration) =>
        configuration.Action switch
        {
            ObsActionType.SwitchScene =>
                $"Switch scene to {Fallback(configuration.SceneName, "(not selected)")}",
            ObsActionType.SetInputMute =>
                $"{(configuration.Muted ? "Mute" : "Unmute")} " +
                Fallback(configuration.InputName, "(not selected)"),
            ObsActionType.SetSourceVisibility =>
                $"{(configuration.Visible ? "Show" : "Hide")} " +
                Fallback(configuration.SourceName, "(not selected)"),
            _ => "OBS action"
        };

    private async Task CompleteActionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.ExecuteActionAsync(_configuration, cancellationToken)
                .ConfigureAwait(false);
            bool clearError = false;

            lock (_stateLock)
            {
                if (_disposed)
                    return;

                CompleteOperationNoLock();
                _activationComplete = _profileActive;
                _recoveryBudget.Reset();

                if (_errorActive)
                {
                    _errorActive = false;
                    clearError = true;
                }
            }

            if (clearError)
                ErrorCleared?.Invoke(this);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_stateLock)
            {
                if (!_disposed)
                    CompleteOperationNoLock();
            }
        }
        catch (Exception ex)
        {
            string failureMessage;
            lock (_stateLock)
            {
                if (_disposed)
                    return;

                CompleteOperationNoLock();
                DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
                _recoveryBudget.RecordFailure(nowUtc);
                _nextAttemptUtc = _recoveryBudget.NextAttemptUtc;
                _errorActive = true;
                failureMessage = _recoveryBudget.DescribeFailure(
                    $"OBS action '{DisplayName}' failed. {ex.Message}"
                );
            }

            ErrorOccurred?.Invoke(this, failureMessage);
        }
    }

    private void CompleteOperationNoLock()
    {
        _operationPending = false;
        _operationCancellation?.Dispose();
        _operationCancellation = null;
    }

    private static string Fallback(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
