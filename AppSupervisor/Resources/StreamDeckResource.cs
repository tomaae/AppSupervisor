using AppSupervisor.Core;
using AppSupervisor.Notifications;
using AppSupervisor.StreamDeck;

namespace AppSupervisor.Resources;

/// <summary>Runs one Stream Deck MCP action when its profile activates.</summary>
internal sealed class StreamDeckResource :
    IManagedResource,
    IManagedResourceReadiness,
    IManagedResourceDeactivationState,
    IManagedResourceLifecycleWork,
    IPauseDrainWork,
    IRecoverableResourceErrorSource
{
    private readonly object _stateLock = new();
    private readonly StreamDeckResourceConfig _configuration;
    private readonly IStreamDeckMcpClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly AutomaticRecoveryBudget _recoveryBudget = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _operationCancellation;
    private bool _operationQueued;
    private bool _operationPending;
    private StreamDeckOperation _queuedOperation;
    private StreamDeckOperation _pendingOperation;
    private bool _profileActive;
    private bool _activationComplete;
    private bool _switchApplied;
    private bool _errorActive;
    private DateTime _nextAttemptUtc;
    private bool _disposed;

    internal StreamDeckResource(StreamDeckResourceConfig configuration)
        : this(configuration, StreamDeckMcpClient.Shared, SupervisorTime.Provider)
    {
    }

    internal StreamDeckResource(
        StreamDeckResourceConfig configuration,
        IStreamDeckMcpClient client,
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
                    _operationQueued || _operationPending || NeedsOperationNoLock()
                );
        }
    }

    public DateTime? NextLifecycleDueUtc
    {
        get
        {
            lock (_stateLock)
            {
                return !_operationQueued && !_operationPending && NeedsOperationNoLock()
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
                return !_profileActive && (
                    _operationQueued || _operationPending ||
                    (RestoresSwitch && _switchApplied)
                );
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
            QueueRequiredOperationNoLock();
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
            if (_disposed || !_profileActive || _activationComplete)
            {
                return ManagedResourceUpdate.None;
            }

            if (!_recoveryBudget.Exhausted &&
                _timeProvider.GetUtcNow().UtcDateTime >= _nextAttemptUtc)
                QueueRequiredOperationNoLock();

            return ManagedResourceUpdate.None;
        }
    }

    public void CancelPendingRecovery()
    {
    }

    public void SuspendMonitoring()
    {
    }

    public void Deactivate()
    {
        lock (_stateLock)
        {
            _profileActive = false;
            _activationComplete = false;
            _recoveryBudget.Reset();
            QueueRequiredOperationNoLock();
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

            if (!_operationQueued && nowUtc >= _nextAttemptUtc)
                QueueRequiredOperationNoLock();

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
            _pendingOperation = _queuedOperation;
            _queuedOperation = StreamDeckOperation.None;
            _operationCancellation?.Dispose();
            _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token
            );
            _ = CompleteActionAsync(_pendingOperation, _operationCancellation.Token);
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
            _queuedOperation = StreamDeckOperation.None;
            _lifetimeCancellation.Cancel();
            _operationCancellation?.Cancel();
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            _operationPending = false;
            ErrorOccurred = null;
            ErrorCleared = null;
        }

        _lifetimeCancellation.Dispose();
    }

    internal static string GetDisplayName(StreamDeckResourceConfig configuration) =>
        string.IsNullOrWhiteSpace(configuration.ActionName)
            ? "New Stream Deck action"
            : $"{configuration.ActionName} ({(configuration.IsSwitch ? "switch" : "button")})";

    private bool RestoresSwitch =>
        _configuration.IsSwitch && _configuration.RestoreSwitchOnDeactivate;

    private async Task CompleteActionAsync(
        StreamDeckOperation operation,
        CancellationToken cancellationToken)
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

                if (operation == StreamDeckOperation.Activate)
                {
                    if (RestoresSwitch)
                        _switchApplied = true;

                    _activationComplete = _profileActive;
                }
                else if (operation == StreamDeckOperation.Restore)
                {
                    _switchApplied = false;
                }

                _recoveryBudget.Reset();

                if (_errorActive)
                {
                    _errorActive = false;
                    clearError = true;
                }

                QueueRequiredOperationNoLock();
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

                if (operation == StreamDeckOperation.Restore && _recoveryBudget.Exhausted)
                    _switchApplied = false;

                failureMessage = _recoveryBudget.DescribeFailure(
                    $"Stream Deck action '{DisplayName}' " +
                    $"{(operation == StreamDeckOperation.Restore ? "restoration" : "activation")} " +
                    $"failed. {ex.Message}"
                );
            }

            ErrorOccurred?.Invoke(this, failureMessage);
        }
    }

    private void CompleteOperationNoLock()
    {
        _operationPending = false;
        _pendingOperation = StreamDeckOperation.None;
        _operationCancellation?.Dispose();
        _operationCancellation = null;
    }

    private void QueueRequiredOperationNoLock()
    {
        if (_disposed || _operationQueued || _operationPending || _recoveryBudget.Exhausted)
            return;

        if (_profileActive)
        {
            if (!_activationComplete)
            {
                if (RestoresSwitch && _switchApplied)
                    _activationComplete = true;
                else
                    QueueOperationNoLock(StreamDeckOperation.Activate);
            }

            return;
        }

        if (RestoresSwitch && _switchApplied)
            QueueOperationNoLock(StreamDeckOperation.Restore);
    }

    private bool NeedsOperationNoLock()
    {
        if (_recoveryBudget.Exhausted)
            return false;

        return _profileActive
            ? !_activationComplete && !(RestoresSwitch && _switchApplied)
            : RestoresSwitch && _switchApplied;
    }

    private void QueueOperationNoLock(StreamDeckOperation operation)
    {
        _queuedOperation = operation;
        _operationQueued = true;
    }

    private enum StreamDeckOperation
    {
        None,
        Activate,
        Restore
    }
}
