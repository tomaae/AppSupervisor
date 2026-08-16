using AppSupervisor.Core;
using AppSupervisor.Notifications;
using AppSupervisor.Twitch;

namespace AppSupervisor.Resources;

/// <summary>Executes Twitch broadcaster actions and restores chat modes after profile deactivation.</summary>
internal sealed class TwitchResource :
    IManagedResource,
    IManagedResourceReadiness,
    IManagedResourceDeactivationState,
    IManagedResourceLifecycleWork,
    IPauseDrainWork,
    IRecoverableResourceErrorSource
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private readonly object _stateLock = new();
    private readonly TwitchResourceConfig _configuration;
    private readonly ITwitchApiClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _operationCancellation;
    private TwitchOperation? _queuedOperation;
    private TwitchOperation? _pendingOperation;
    private TwitchChatSettings? _originalSettings;
    private bool _settingApplied;
    private bool _profileActive;
    private bool _activationComplete;
    private bool _errorActive;
    private DateTime _nextAttemptUtc;
    private bool _disposed;

    internal TwitchResource(TwitchResourceConfig configuration, TwitchIntegrationConfig integration)
        : this(configuration, new TwitchApiClient(integration), SupervisorTime.Provider)
    {
    }

    internal TwitchResource(
        TwitchResourceConfig configuration,
        ITwitchApiClient client,
        TimeProvider? timeProvider = null)
    {
        _configuration = configuration;
        _client = client;
        _timeProvider = timeProvider ?? SupervisorTime.Provider;
    }

    public event Action<IManagedResource, string>? ErrorOccurred;
    public event Action<IManagedResource>? ErrorCleared;
    public string DisplayName => GetDisplayName(_configuration);
    public IReadOnlyList<NotificationTarget> NotificationTargets => _configuration.Notifications.Target;

    public bool LifecycleWorkPending
    {
        get { lock (_stateLock) return !_disposed && (_queuedOperation.HasValue || _pendingOperation.HasValue); }
    }

    public bool DeactivationPending
    {
        get
        {
            lock (_stateLock)
                return !_disposed && !_profileActive && IsReversible &&
                    (_settingApplied || _queuedOperation.HasValue || _pendingOperation.HasValue);
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
            if (!_queuedOperation.HasValue && !_pendingOperation.HasValue)
                _queuedOperation = TwitchOperation.Activate;
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
            if (!_disposed && _profileActive && !_activationComplete &&
                !_queuedOperation.HasValue && !_pendingOperation.HasValue &&
                _timeProvider.GetUtcNow().UtcDateTime >= _nextAttemptUtc)
            {
                _queuedOperation = TwitchOperation.Activate;
            }
            return ManagedResourceUpdate.None;
        }
    }

    public void CancelPendingRecovery() { }
    public void SuspendMonitoring() { }

    public void Deactivate()
    {
        lock (_stateLock)
        {
            _profileActive = false;
            _activationComplete = false;
            if (IsReversible && _settingApplied && !_queuedOperation.HasValue && !_pendingOperation.HasValue)
                _queuedOperation = TwitchOperation.Restore;
            else if (!IsReversible && _queuedOperation == TwitchOperation.Activate)
                _queuedOperation = null;
        }
    }

    public void SuperviseDeactivation()
    {
        lock (_stateLock)
        {
            if (!_disposed && !_profileActive && IsReversible && _settingApplied &&
                !_queuedOperation.HasValue && !_pendingOperation.HasValue &&
                _timeProvider.GetUtcNow().UtcDateTime >= _nextAttemptUtc)
            {
                _queuedOperation = TwitchOperation.Restore;
            }
        }
    }

    public ManagedResourceUpdate AdvanceLifecycle(DateTime nowUtc)
    {
        TwitchOperation operation;
        CancellationToken cancellationToken;
        lock (_stateLock)
        {
            if (_disposed || _pendingOperation.HasValue || !_queuedOperation.HasValue)
                return ManagedResourceUpdate.None;
            operation = _queuedOperation.Value;
            _queuedOperation = null;
            _pendingOperation = operation;
            _operationCancellation?.Dispose();
            _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            cancellationToken = _operationCancellation.Token;
        }
        _ = CompleteOperationAsync(operation, cancellationToken);
        return ManagedResourceUpdate.None;
    }

    public void BeginPauseDrain() { }
    public void AdvancePauseDrain() { }

    private async Task CompleteOperationAsync(TwitchOperation operation, CancellationToken cancellationToken)
    {
        try
        {
            if (operation == TwitchOperation.Activate)
                await ActivateAsync(cancellationToken).ConfigureAwait(false);
            else
                await RestoreAsync(cancellationToken).ConfigureAwait(false);

            bool clearError;
            lock (_stateLock)
            {
                if (_disposed)
                    return;
                CompletePendingNoLock();
                if (operation == TwitchOperation.Activate)
                {
                    _activationComplete = _profileActive;
                    if (!_profileActive && IsReversible && _settingApplied)
                        _queuedOperation = TwitchOperation.Restore;
                }
                else
                {
                    _settingApplied = false;
                    _originalSettings = null;
                }
                clearError = _errorActive;
                _errorActive = false;
            }
            if (clearError)
                ErrorCleared?.Invoke(this);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_stateLock)
                if (!_disposed) CompletePendingNoLock();
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                if (_disposed)
                    return;
                CompletePendingNoLock();
                _nextAttemptUtc = _timeProvider.GetUtcNow().UtcDateTime + RetryInterval;
                _errorActive = true;
            }
            ErrorOccurred?.Invoke(this, $"Twitch action '{DisplayName}' failed. {ex.Message}");
        }
    }

    private async Task ActivateAsync(CancellationToken cancellationToken)
    {
        switch (_configuration.Action)
        {
            case TwitchActionType.SendChatMessage:
                await _client.SendChatMessageAsync(_configuration.Message, cancellationToken).ConfigureAwait(false);
                return;
            case TwitchActionType.RunCommercial:
                await _client.RunCommercialAsync(_configuration.CommercialLengthSeconds, cancellationToken).ConfigureAwait(false);
                return;
        }

        TwitchChatSettings original = await _client.GetChatSettingsAsync(cancellationToken).ConfigureAwait(false);
        TwitchChatSettingsUpdate update = CreateActivationUpdate(_configuration);
        await _client.UpdateChatSettingsAsync(update, cancellationToken).ConfigureAwait(false);
        lock (_stateLock)
        {
            if (_disposed)
                return;
            _originalSettings = original;
            _settingApplied = true;
        }
    }

    private Task RestoreAsync(CancellationToken cancellationToken)
    {
        TwitchChatSettings original;
        lock (_stateLock)
            original = _originalSettings ?? throw new InvalidOperationException("The original Twitch chat settings are unavailable.");
        return _client.UpdateChatSettingsAsync(CreateRestoreUpdate(_configuration.Action, original), cancellationToken);
    }

    internal static TwitchChatSettingsUpdate CreateActivationUpdate(TwitchResourceConfig configuration) =>
        configuration.Action switch
        {
            TwitchActionType.EmoteOnly => new() { EmoteMode = configuration.ModeEnabled },
            TwitchActionType.FollowersOnly => new()
            {
                FollowerMode = configuration.ModeEnabled,
                FollowerModeDuration = configuration.ModeEnabled ? configuration.FollowerDurationMinutes : null
            },
            TwitchActionType.SlowMode => new()
            {
                SlowMode = configuration.ModeEnabled,
                SlowModeWaitTime = configuration.ModeEnabled ? configuration.SlowModeWaitSeconds : null
            },
            TwitchActionType.SubscribersOnly => new() { SubscriberMode = configuration.ModeEnabled },
            _ => throw new InvalidOperationException("This Twitch action is not a reversible chat mode.")
        };

    internal static TwitchChatSettingsUpdate CreateRestoreUpdate(
        TwitchActionType action,
        TwitchChatSettings original) => action switch
        {
            TwitchActionType.EmoteOnly => new() { EmoteMode = original.EmoteMode },
            TwitchActionType.FollowersOnly => new()
            {
                FollowerMode = original.FollowerMode,
                FollowerModeDuration = original.FollowerMode ? original.FollowerModeDuration ?? 0 : null
            },
            TwitchActionType.SlowMode => new()
            {
                SlowMode = original.SlowMode,
                SlowModeWaitTime = original.SlowMode ? original.SlowModeWaitTime ?? 30 : null
            },
            TwitchActionType.SubscribersOnly => new() { SubscriberMode = original.SubscriberMode },
            _ => throw new InvalidOperationException("This Twitch action is not a reversible chat mode.")
        };

    internal static string GetDisplayName(TwitchResourceConfig configuration) => configuration.Action switch
    {
        TwitchActionType.SendChatMessage => $"Send chat message: {Abbreviate(configuration.Message)}",
        TwitchActionType.RunCommercial => $"Run {configuration.CommercialLengthSeconds}-second ad",
        TwitchActionType.EmoteOnly => $"Emote-only {ModeText(configuration.ModeEnabled)}",
        TwitchActionType.FollowersOnly => $"Followers-only {ModeText(configuration.ModeEnabled)}",
        TwitchActionType.SlowMode => $"Slow mode {ModeText(configuration.ModeEnabled)}",
        TwitchActionType.SubscribersOnly => $"Subscribers-only {ModeText(configuration.ModeEnabled)}",
        _ => "Twitch action"
    };

    private bool IsReversible => _configuration.Action is TwitchActionType.EmoteOnly or
        TwitchActionType.FollowersOnly or TwitchActionType.SlowMode or TwitchActionType.SubscribersOnly;

    private static string ModeText(bool enabled) => enabled ? "on while active" : "off while active";
    private static string Abbreviate(string text)
    {
        string value = string.IsNullOrWhiteSpace(text) ? "(empty)" : text.Trim();
        return value.Length <= 40 ? value : value[..37] + "...";
    }

    private void CompletePendingNoLock()
    {
        _pendingOperation = null;
        _operationCancellation?.Dispose();
        _operationCancellation = null;
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _queuedOperation = null;
            _lifetimeCancellation.Cancel();
            _operationCancellation?.Cancel();
            _operationCancellation?.Dispose();
            _client.Dispose();
            ErrorOccurred = null;
            ErrorCleared = null;
        }
        _lifetimeCancellation.Dispose();
    }

    private enum TwitchOperation { Activate, Restore }
}
