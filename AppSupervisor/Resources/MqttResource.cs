using System.Text;
using AppSupervisor.Core;
using AppSupervisor.Mqtt;
using AppSupervisor.Notifications;

namespace AppSupervisor.Resources;

/// <summary>
/// Queues one nonblocking MQTT activation publish and performs the selected deterministic inverse
/// when the profile deactivates. Retained-state restoration captures before publishing.
/// </summary>
internal sealed class MqttResource :
    IManagedResource,
    IManagedResourceReadiness,
    IManagedResourceDeactivationState,
    IManagedResourceLifecycleWork,
    IPauseDrainWork,
    IRecoverableResourceErrorSource
{
    private readonly object _stateLock = new();
    private readonly MqttResourceConfig _configuration;
    private readonly IMqttBrokerClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly AutomaticRecoveryBudget _activationBudget = new();
    private readonly AutomaticRecoveryBudget _deactivationBudget = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Queue<MqttOperation> _operations = new();
    private CancellationTokenSource? _operationCancellation;
    private MqttOperation? _currentOperation;
    private MqttOperation? _retryOperation;
    private DateTime _nextAttemptUtc;
    private byte[]? _capturedState;
    private bool _profileActive;
    private bool _activationComplete;
    private bool _activationPublishAccepted;
    private bool _errorActive;
    private bool _disposed;

    internal MqttResource(MqttResourceConfig configuration, MqttIntegrationConfig integration)
        : this(configuration, new MqttBrokerClient(integration), SupervisorTime.Provider)
    {
    }

    internal MqttResource(
        MqttResourceConfig configuration,
        IMqttBrokerClient client,
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
                    _currentOperation is not null ||
                    _operations.Count > 0 ||
                    _retryOperation is not null
                );
        }
    }

    public DateTime? NextLifecycleDueUtc
    {
        get
        {
            lock (_stateLock)
            {
                return _currentOperation is null && _operations.Count == 0 &&
                    _retryOperation is not null
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
            {
                return !_profileActive && (
                    _currentOperation is not null ||
                    _operations.Count > 0 ||
                    _retryOperation is not null
                );
            }
        }
    }

    public bool PauseDrainPending => LifecycleWorkPending;

    public void Activate()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;

            if (!_profileActive)
                _activationBudget.Reset();

            _profileActive = true;
            _activationComplete = false;
            _activationPublishAccepted = false;
            EnqueueNoLock(MqttOperation.Activation);
        }
    }

    public bool IsStarted()
    {
        lock (_stateLock)
            return !_disposed && _profileActive && _activationComplete;
    }

    public ManagedResourceUpdate Supervise() => ManagedResourceUpdate.None;

    public void CancelPendingRecovery()
    {
        // Work already accepted by the ordered lifecycle drains; no continuous monitor exists.
    }

    public void Deactivate()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;

            _profileActive = false;
            _activationComplete = false;
            _deactivationBudget.Reset();
            QueueInverseIfRequiredNoLock();
        }
    }

    public void SuperviseDeactivation()
    {
    }

    public ManagedResourceUpdate AdvanceLifecycle(DateTime nowUtc)
    {
        lock (_stateLock)
        {
            if (_disposed || _currentOperation is not null)
                return ManagedResourceUpdate.None;

            MqttOperation? operation = null;

            if (_operations.Count > 0)
            {
                operation = _operations.Dequeue();
            }
            else if (_retryOperation is not null && nowUtc >= _nextAttemptUtc)
            {
                operation = _retryOperation;
                _retryOperation = null;
            }

            if (operation is null)
                return ManagedResourceUpdate.None;

            AutomaticRecoveryBudget budget = GetBudget(operation.Value);

            if (!budget.TryBeginAttempt(nowUtc))
                return ManagedResourceUpdate.None;

            StartOperationNoLock(operation.Value);
            return ManagedResourceUpdate.None;
        }
    }

    public void BeginPauseDrain()
    {
    }

    public void AdvancePauseDrain()
    {
    }

    public void SuspendMonitoring()
    {
        // MQTT has no continuous monitoring; accepted publish/restoration work drains.
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _operations.Clear();
            _retryOperation = null;
            _lifetimeCancellation.Cancel();
            _operationCancellation?.Cancel();
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            _currentOperation = null;
            _client.Dispose();
            ErrorOccurred = null;
            ErrorCleared = null;
        }

        _lifetimeCancellation.Dispose();
    }

    internal static string GetDisplayName(MqttResourceConfig configuration) =>
        string.IsNullOrEmpty(configuration.Topic)
            ? "MQTT publish"
            : $"Publish to {configuration.Topic}";

    private void EnqueueNoLock(MqttOperation operation)
    {
        if (_currentOperation == operation || _retryOperation == operation ||
            _operations.Contains(operation))
        {
            return;
        }

        _operations.Enqueue(operation);
    }

    private void QueueInverseIfRequiredNoLock()
    {
        if (_profileActive)
            return;

        switch (_configuration.DeactivationBehavior)
        {
            case MqttDeactivationBehavior.PublishConfiguredPayload
                when _activationPublishAccepted:
                EnqueueNoLock(MqttOperation.Deactivation);
                break;
            case MqttDeactivationBehavior.RestoreRetainedState
                when _activationPublishAccepted && _capturedState is not null:
                EnqueueNoLock(MqttOperation.Deactivation);
                break;
        }
    }

    private void StartOperationNoLock(MqttOperation operation)
    {
        _operationCancellation?.Dispose();
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token
        );
        _currentOperation = operation;
        _ = CompleteOperationAsync(operation, _operationCancellation.Token);
    }

    private async Task CompleteOperationAsync(
        MqttOperation operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteOperationAsync(operation, cancellationToken).ConfigureAwait(false);
            bool clearError = false;

            lock (_stateLock)
            {
                if (_disposed || _currentOperation != operation)
                    return;

                CompleteCurrentOperationNoLock();
                GetBudget(operation).Reset();

                if (operation == MqttOperation.Activation)
                {
                    _activationComplete = _profileActive;
                    QueueInverseIfRequiredNoLock();
                }
                else
                {
                    _capturedState = null;
                    _activationPublishAccepted = false;
                }

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
                if (_currentOperation == operation)
                    CompleteCurrentOperationNoLock();
            }
        }
        catch (Exception exception)
        {
            string failure;

            lock (_stateLock)
            {
                if (_disposed || _currentOperation != operation)
                    return;

                CompleteCurrentOperationNoLock();
                AutomaticRecoveryBudget budget = GetBudget(operation);
                DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
                budget.RecordFailure(nowUtc);

                if (operation == MqttOperation.Activation &&
                    !_activationPublishAccepted)
                {
                    // A retained value captured before an unaccepted publish may become stale
                    // before retry. Capture again immediately before the next real publish.
                    _capturedState = null;
                }

                bool shouldRetry = operation == MqttOperation.Deactivation || _profileActive;

                if (shouldRetry && !budget.Exhausted)
                {
                    _retryOperation = operation;
                    _nextAttemptUtc = budget.NextAttemptUtc;
                }
                else if (operation == MqttOperation.Activation)
                {
                    QueueInverseIfRequiredNoLock();
                }

                _errorActive = true;
                failure = budget.DescribeFailure(
                    $"MQTT {OperationName(operation)} failed for '{_configuration.Topic}'. " +
                    exception.Message
                );
            }

            ErrorOccurred?.Invoke(this, failure);
        }
    }

    private Task ExecuteOperationAsync(
        MqttOperation operation,
        CancellationToken cancellationToken)
    {
        TimeSpan timeout = TimeSpan.FromSeconds(_configuration.VerificationTimeoutSeconds);

        if (operation == MqttOperation.Activation)
        {
            MqttStateCheck? verification = _configuration.VerifyStateChange
                ? new MqttStateCheck(
                    _configuration.VerificationTopic,
                    Encoding.UTF8.GetBytes(_configuration.ExpectedState),
                    timeout
                )
                : null;
            MqttRetainedStateCapture? capture =
                _configuration.DeactivationBehavior ==
                    MqttDeactivationBehavior.RestoreRetainedState &&
                _capturedState is null
                    ? new MqttRetainedStateCapture(
                        _configuration.VerificationTopic,
                        timeout
                    )
                    : null;
            return _client.PublishAsync(
                new MqttPublishMessage(
                    _configuration.Topic,
                    Encoding.UTF8.GetBytes(_configuration.Payload),
                    _configuration.Qos,
                    _configuration.Retain
                ),
                verification,
                capture,
                RecordCapturedState,
                RecordActivationPublishAccepted,
                cancellationToken
            );
        }

        byte[] payload = _configuration.DeactivationBehavior ==
            MqttDeactivationBehavior.RestoreRetainedState
                ? _capturedState ?? throw new InvalidOperationException(
                    "The retained pre-activation state is unavailable; no unsafe inverse was sent."
                )
                : Encoding.UTF8.GetBytes(_configuration.DeactivationPayload);
        MqttStateCheck? reverseVerification = _configuration.DeactivationBehavior ==
            MqttDeactivationBehavior.RestoreRetainedState
                ? new MqttStateCheck(_configuration.VerificationTopic, payload, timeout)
                : _configuration.VerifyDeactivation
                    ? new MqttStateCheck(
                        _configuration.VerificationTopic,
                        Encoding.UTF8.GetBytes(_configuration.DeactivationExpectedState),
                        timeout
                    )
                    : null;
        return _client.PublishAsync(
            new MqttPublishMessage(
                _configuration.DeactivationTopic,
                payload,
                _configuration.DeactivationQos,
                _configuration.DeactivationRetain
            ),
            reverseVerification,
            capture: null,
            stateCaptured: null,
            publishAccepted: null,
            cancellationToken
        );
    }

    private void RecordCapturedState(byte[] payload)
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;

            _capturedState = [.. payload];
        }
    }

    private void RecordActivationPublishAccepted()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;

            _activationPublishAccepted = true;
            QueueInverseIfRequiredNoLock();
        }
    }

    private AutomaticRecoveryBudget GetBudget(MqttOperation operation) =>
        operation == MqttOperation.Activation ? _activationBudget : _deactivationBudget;

    private void CompleteCurrentOperationNoLock()
    {
        _currentOperation = null;
        _operationCancellation?.Dispose();
        _operationCancellation = null;
    }

    private static string OperationName(MqttOperation operation) =>
        operation == MqttOperation.Activation ? "activation publish" : "inverse publish";

    private enum MqttOperation
    {
        Activation,
        Deactivation
    }
}
