using AppSupervisor.Core;
using AppSupervisor.HomeAssistant;
using AppSupervisor.Notifications;

namespace AppSupervisor.Resources;

/// <summary>
/// Applies one deterministic Home Assistant action asynchronously and optionally keeps its state persistent.
/// </summary>
internal sealed class HomeAssistantResource :
    IManagedResource,
    IManagedResourceReadiness,
    IManagedResourceDeactivationState,
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
    private CancellationTokenSource? _operationCancellation;
    private int _operationGeneration;
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
        : this(configuration, new HomeAssistantClient(integration), TimeProvider.System)
    {
    }

    internal HomeAssistantResource(
        HomeAssistantResourceConfig configuration,
        IHomeAssistantClient client,
        TimeProvider? timeProvider = null)
    {
        _configuration = configuration;
        _client = client;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event Action<IManagedResource, string>? ErrorOccurred;

    public event Action<IManagedResource>? ErrorCleared;

    public string DisplayName => string.IsNullOrWhiteSpace(_configuration.EntityName)
        ? _configuration.EntityId
        : _configuration.EntityName;

    public IReadOnlyList<NotificationTarget> NotificationTargets =>
        _configuration.Notifications.Target;

    /// <summary>Gets whether the profile's inverse Home Assistant action is still running.</summary>
    public bool DeactivationPending
    {
        get
        {
            lock (_stateLock)
                return !_profileActive && _operationPending;
        }
    }

    /// <summary>Issues the configured service call without blocking the startup scheduler.</summary>
    public void Activate()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;

            _profileActive = true;
            _persistenceSuspended = false;
            _activationComplete = false;
            StartOperationNoLock(
                cancellationToken => ApplyServiceAsync(
                    _configuration.Service,
                    DesiredState,
                    _configuration.VerifyStateChange,
                    cancellationToken
                ),
                completesActivation: true,
                reportsRestoration: false
            );
        }
    }

    /// <summary>Reports readiness only after the initial service call and optional verification complete.</summary>
    public bool IsStarted()
    {
        lock (_stateLock)
            return !_disposed && _activationComplete;
    }

    /// <summary>Schedules retry or one-minute persistence work and reports completed corrections.</summary>
    public ManagedResourceUpdate Supervise()
    {
        lock (_stateLock)
        {
            if (_disposed || !_profileActive)
                return ManagedResourceUpdate.None;

            _persistenceSuspended = false;
            bool restored = _stateRestored;
            _stateRestored = false;

            if (!_operationPending && UtcNow >= _nextOperationUtc)
            {
                if (!_activationComplete)
                {
                    StartOperationNoLock(
                        cancellationToken => ApplyServiceAsync(
                            _configuration.Service,
                            DesiredState,
                            _configuration.VerifyStateChange,
                            cancellationToken
                        ),
                        completesActivation: true,
                        reportsRestoration: false
                    );
                }
                else if (_configuration.Persistent && !_persistenceSuspended && IsStateful)
                {
                    StartOperationNoLock(
                        CheckAndRestoreAsync,
                        completesActivation: false,
                        reportsRestoration: true
                    );
                }
            }

            return restored
                ? ManagedResourceUpdate.Restarted
                : ManagedResourceUpdate.None;
        }
    }

    /// <summary>Stops persistence while the profile waits through its close timeout.</summary>
    public void CancelPendingRecovery()
    {
        lock (_stateLock)
        {
            _persistenceSuspended = true;

            if (_activationComplete)
                CancelOperationNoLock();
        }
    }

    /// <summary>Cancels network work without changing the external entity while AppSupervisor is paused.</summary>
    public void SuspendMonitoring()
    {
        lock (_stateLock)
        {
            _persistenceSuspended = true;
            CancelOperationNoLock();
        }
    }

    /// <summary>Applies the deterministic inverse service after the profile close timeout.</summary>
    public void Deactivate()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;

            _profileActive = false;
            _activationComplete = false;
            _persistenceSuspended = true;
            CancelOperationNoLock();
            string? reverseService = ReverseService;

            if (reverseService is null)
                return;

            StartOperationNoLock(
                cancellationToken => ApplyServiceAsync(
                    reverseService,
                    ReverseDesiredState,
                    _configuration.VerifyStateChange,
                    cancellationToken
                ),
                completesActivation: false,
                reportsRestoration: false
            );
        }
    }

    public void SuperviseDeactivation() { }

    /// <summary>Cancels outstanding calls and releases the private HTTP client without changing HA state.</summary>
    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _lifetimeCancellation.Cancel();
            CancelOperationNoLock();
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

    private void StartOperationNoLock(
        Func<CancellationToken, Task<bool>> operation,
        bool completesActivation,
        bool reportsRestoration)
    {
        CancelOperationNoLock();
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token
        );
        int generation = ++_operationGeneration;
        _operationPending = true;
        _ = CompleteOperationAsync(
            generation,
            operation,
            completesActivation,
            reportsRestoration,
            _operationCancellation.Token
        );
    }

    private async Task CompleteOperationAsync(
        int generation,
        Func<CancellationToken, Task<bool>> operation,
        bool completesActivation,
        bool reportsRestoration,
        CancellationToken cancellationToken)
    {
        try
        {
            bool restored = await operation(cancellationToken).ConfigureAwait(false);
            bool clearError = false;

            lock (_stateLock)
            {
                if (_disposed || generation != _operationGeneration)
                    return;

                _operationPending = false;
                _nextOperationUtc = UtcNow + PersistenceInterval;

                if (completesActivation)
                    _activationComplete = true;
                if (reportsRestoration && restored)
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
                if (generation == _operationGeneration)
                    _operationPending = false;
            }
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                if (_disposed || generation != _operationGeneration)
                    return;

                _operationPending = false;
                _nextOperationUtc = UtcNow + PersistenceInterval;
                _errorActive = true;
            }

            ErrorOccurred?.Invoke(
                this,
                $"Home Assistant action '{_configuration.Service}' failed for " +
                $"'{_configuration.EntityId}'. {ex.Message}"
            );
        }
    }

    private void CancelOperationNoLock()
    {
        _operationGeneration++;
        _operationPending = false;
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = null;
    }

}
