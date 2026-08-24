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
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private readonly object _stateLock = new();
    private readonly StreamDeckResourceConfig _configuration;
    private readonly IStreamDeckMcpClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _operationCancellation;
    private bool _operationQueued;
    private bool _operationPending;
    private bool _profileActive;
    private bool _activationComplete;
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
                return !_disposed && (_operationQueued || _operationPending);
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

    public bool PauseDrainPending => LifecycleWorkPending;

    public void Activate()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;

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

            if (_timeProvider.GetUtcNow().UtcDateTime >= _nextAttemptUtc)
                _operationQueued = true;

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
        }
    }

    public void SuperviseDeactivation()
    {
    }

    public ManagedResourceUpdate AdvanceLifecycle(DateTime nowUtc)
    {
        lock (_stateLock)
        {
            if (_disposed || _operationPending || !_operationQueued)
                return ManagedResourceUpdate.None;

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
            ErrorOccurred = null;
            ErrorCleared = null;
        }

        _lifetimeCancellation.Dispose();
    }

    internal static string GetDisplayName(StreamDeckResourceConfig configuration) =>
        string.IsNullOrWhiteSpace(configuration.ActionName)
            ? "New Stream Deck action"
            : configuration.ActionName;

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
            lock (_stateLock)
            {
                if (_disposed)
                    return;

                CompleteOperationNoLock();
                _nextAttemptUtc = _timeProvider.GetUtcNow().UtcDateTime + RetryInterval;
                _errorActive = true;
            }

            ErrorOccurred?.Invoke(
                this,
                $"Stream Deck action '{DisplayName}' failed. {ex.Message}"
            );
        }
    }

    private void CompleteOperationNoLock()
    {
        _operationPending = false;
        _operationCancellation?.Dispose();
        _operationCancellation = null;
    }
}
