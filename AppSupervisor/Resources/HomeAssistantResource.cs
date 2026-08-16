using AppSupervisor.Core;
using AppSupervisor.HomeAssistant;
using AppSupervisor.Notifications;

namespace AppSupervisor.Resources;

/// <summary>
/// Queues deterministic Home Assistant actions for the serialized lifecycle timer and optionally
/// keeps the requested state persistent.
/// </summary>
internal sealed class HomeAssistantResource :
    IManagedResource,
    IManagedResourceReadiness,
    IManagedResourceDeactivationState,
    IManagedResourceLifecycleWork,
    IPauseDrainWork,
    IRecoverableResourceErrorSource
{
    private static readonly TimeSpan PersistenceInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan VerificationDelay = TimeSpan.FromMilliseconds(400);
    private const int VerificationAttempts = 5;

    private readonly object _stateLock = new();
    private readonly HomeAssistantResourceConfig _configuration;
    private readonly IHomeAssistantClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Queue<HomeAssistantOperation> _operations = new();
    private CancellationTokenSource? _operationCancellation;
    private HomeAssistantOperation? _currentOperation;
    private bool _operationPending;
    private bool _profileActive;
    private bool _activationComplete;
    private bool _persistenceSuspended;
    private bool _stateRestored;
    private bool _errorActive;
    private DateTime _nextOperationUtc;
    private bool _disposed;

    internal HomeAssistantResource(
        HomeAssistantResourceConfig configuration,
        HomeAssistantIntegrationConfig integration)
        : this(configuration, new HomeAssistantClient(integration), SupervisorTime.Provider)
    {
    }

    internal HomeAssistantResource(
        HomeAssistantResourceConfig configuration,
        IHomeAssistantClient client,
        TimeProvider? timeProvider = null)
    {
        _configuration = configuration;
        _client = client;
        _timeProvider = timeProvider ?? SupervisorTime.Provider;
    }

    public event Action<IManagedResource, string>? ErrorOccurred;

    public event Action<IManagedResource>? ErrorCleared;

    public string DisplayName => string.IsNullOrWhiteSpace(_configuration.EntityName)
        ? _configuration.EntityId
        : _configuration.EntityName;

    public IReadOnlyList<NotificationTarget> NotificationTargets =>
        _configuration.Notifications.Target;

    public bool LifecycleWorkPending
    {
        get
        {
            lock (_stateLock)
                return !_disposed && (_operationPending || _operations.Count > 0);
        }
    }

    public bool DeactivationPending
    {
        get
        {
            lock (_stateLock)
                return !_profileActive && (_operationPending || _operations.Count > 0);
        }
    }

    public bool PauseDrainPending
    {
        get
        {
            lock (_stateLock)
                return !_disposed && (_operationPending || _operations.Count > 0);
        }
    }

    /// <summary>Queues the configured action; the lifecycle timer starts it in order.</summary>
    public void Activate()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;

            _profileActive = true;
            _persistenceSuspended = false;
            _activationComplete = false;
            EnqueueNoLock(HomeAssistantOperation.Activation);
        }
    }

    public bool IsStarted()
    {
        lock (_stateLock)
            return !_disposed && _profileActive && _activationComplete;
    }

    /// <summary>Produces due retries and persistence checks; it never starts network work.</summary>
    public ManagedResourceUpdate Supervise()
    {
        lock (_stateLock)
        {
            if (_disposed || !_profileActive)
                return ManagedResourceUpdate.None;

            _persistenceSuspended = false;
            bool restored = _stateRestored;
            _stateRestored = false;

            if (!_operationPending && _operations.Count == 0 && UtcNow >= _nextOperationUtc)
            {
                if (!_activationComplete)
                    EnqueueNoLock(HomeAssistantOperation.Activation);
                else if (_configuration.Persistent && !_persistenceSuspended && IsStateful)
                    EnqueueNoLock(HomeAssistantOperation.Persistence);
            }

            return restored
                ? ManagedResourceUpdate.Restarted
                : ManagedResourceUpdate.None;
        }
    }

    /// <summary>Stops producing persistence checks while preserving already accepted work.</summary>
    public void CancelPendingRecovery()
    {
        lock (_stateLock)
        {
            _persistenceSuspended = true;
            RemoveQueuedPersistenceNoLock();
        }
    }

    /// <summary>Applies the deterministic inverse after all earlier HA work finishes.</summary>
    public void Deactivate()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;

            _profileActive = false;
            _activationComplete = false;
            _persistenceSuspended = true;
            RemoveQueuedPersistenceNoLock();

            if (ReverseService is not null)
                EnqueueNoLock(HomeAssistantOperation.Deactivation);
        }
    }

    public void SuperviseDeactivation() { }

    /// <summary>Starts at most one queued HA action from the serialized lifecycle pass.</summary>
    public ManagedResourceUpdate AdvanceLifecycle(DateTime nowUtc)
    {
        lock (_stateLock)
        {
            if (_disposed || _operationPending || _operations.Count == 0)
                return ManagedResourceUpdate.None;

            StartOperationNoLock(_operations.Dequeue());
            return ManagedResourceUpdate.None;
        }
    }

    public void BeginPauseDrain()
    {
        lock (_stateLock)
        {
            _persistenceSuspended = true;
            RemoveQueuedPersistenceNoLock();
        }
    }

    public void AdvancePauseDrain() { }

    /// <summary>Stops future persistence monitoring after the accepted action queue has drained.</summary>
    public void SuspendMonitoring()
    {
        lock (_stateLock)
        {
            _persistenceSuspended = true;
            RemoveQueuedPersistenceNoLock();
        }
    }

    /// <summary>Cancels outstanding calls on supervisor exit and releases the client.</summary>
    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _operations.Clear();
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

    private bool IsStateful => DesiredState is not null;

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    private string? DesiredState =>
        HomeAssistantServiceSemantics.GetDesiredState(_configuration.Service);

    private string? ReverseDesiredState => ReverseService is null
        ? null
        : HomeAssistantServiceSemantics.GetDesiredState(ReverseService);

    private string? ReverseService =>
        HomeAssistantServiceSemantics.GetReverseService(_configuration.Service);

    private void EnqueueNoLock(HomeAssistantOperation operation)
    {
        if (_operationPending && _operations.Count == 0 && _currentOperation == operation)
            return;

        if (_operations.Count > 0 && _operations.Last() == operation)
            return;

        _operations.Enqueue(operation);
    }

    private void RemoveQueuedPersistenceNoLock()
    {
        if (_operations.Count == 0 || !_operations.Contains(HomeAssistantOperation.Persistence))
            return;

        HomeAssistantOperation[] retained = _operations
            .Where(operation => operation != HomeAssistantOperation.Persistence)
            .ToArray();
        _operations.Clear();

        foreach (HomeAssistantOperation operation in retained)
            _operations.Enqueue(operation);
    }

    private void StartOperationNoLock(HomeAssistantOperation operation)
    {
        _operationCancellation?.Dispose();
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token
        );
        _currentOperation = operation;
        _operationPending = true;
        _ = CompleteOperationAsync(operation, _operationCancellation.Token);
    }

    private async Task CompleteOperationAsync(
        HomeAssistantOperation operation,
        CancellationToken cancellationToken)
    {
        try
        {
            bool restored = operation switch
            {
                HomeAssistantOperation.Activation => await ApplyServiceAsync(
                    _configuration.Service,
                    DesiredState,
                    _configuration.VerifyStateChange,
                    cancellationToken
                ).ConfigureAwait(false),
                HomeAssistantOperation.Deactivation => await ApplyServiceAsync(
                    ReverseService!,
                    ReverseDesiredState,
                    _configuration.VerifyStateChange,
                    cancellationToken
                ).ConfigureAwait(false),
                HomeAssistantOperation.Persistence => await CheckAndRestoreAsync(
                    cancellationToken
                ).ConfigureAwait(false),
                _ => false
            };
            bool clearError = false;

            lock (_stateLock)
            {
                if (_disposed || _currentOperation != operation)
                    return;

                CompleteCurrentOperationNoLock();
                _nextOperationUtc = UtcNow + PersistenceInterval;

                if (operation == HomeAssistantOperation.Activation)
                    _activationComplete = true;
                if (operation == HomeAssistantOperation.Persistence && restored)
                    _stateRestored = true;

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
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                if (_disposed || _currentOperation != operation)
                    return;

                CompleteCurrentOperationNoLock();
                _nextOperationUtc = UtcNow + PersistenceInterval;
                _errorActive = true;
            }

            ErrorOccurred?.Invoke(
                this,
                $"Home Assistant action '{GetServiceName(operation)}' failed for " +
                $"'{_configuration.EntityId}'. {ex.Message}"
            );
        }
    }

    private void CompleteCurrentOperationNoLock()
    {
        _operationPending = false;
        _currentOperation = null;
        _operationCancellation?.Dispose();
        _operationCancellation = null;
    }

    private string GetServiceName(HomeAssistantOperation operation) => operation switch
    {
        HomeAssistantOperation.Deactivation => ReverseService ?? _configuration.Service,
        HomeAssistantOperation.Persistence => $"verify {_configuration.Service}",
        _ => _configuration.Service
    };

    private async Task<bool> ApplyServiceAsync(
        string service,
        string? expectedState,
        bool verify,
        CancellationToken cancellationToken)
    {
        await _client.CallServiceAsync(service, _configuration.EntityId, cancellationToken)
            .ConfigureAwait(false);

        if (verify && expectedState is not null)
            await VerifyStateAsync(expectedState, cancellationToken).ConfigureAwait(false);

        return false;
    }

    private async Task<bool> CheckAndRestoreAsync(CancellationToken cancellationToken)
    {
        string expectedState = DesiredState!;
        string actualState = await _client.GetEntityStateAsync(
            _configuration.EntityId,
            cancellationToken
        ).ConfigureAwait(false);

        if (string.Equals(actualState, expectedState, StringComparison.OrdinalIgnoreCase))
            return false;

        await _client.CallServiceAsync(
            _configuration.Service,
            _configuration.EntityId,
            cancellationToken
        ).ConfigureAwait(false);

        if (_configuration.VerifyStateChange)
            await VerifyStateAsync(expectedState, cancellationToken).ConfigureAwait(false);

        return true;
    }

    private async Task VerifyStateAsync(
        string expectedState,
        CancellationToken cancellationToken)
    {
        string actualState = "unknown";

        for (int attempt = 0; attempt < VerificationAttempts; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(VerificationDelay, cancellationToken).ConfigureAwait(false);

            actualState = await _client.GetEntityStateAsync(
                _configuration.EntityId,
                cancellationToken
            ).ConfigureAwait(false);

            if (string.Equals(actualState, expectedState, StringComparison.OrdinalIgnoreCase))
                return;
        }

        throw new InvalidOperationException(
            $"Home Assistant entity '{_configuration.EntityId}' remained '{actualState}' " +
            $"instead of becoming '{expectedState}'."
        );
    }

    private enum HomeAssistantOperation
    {
        Activation,
        Deactivation,
        Persistence
    }
}
