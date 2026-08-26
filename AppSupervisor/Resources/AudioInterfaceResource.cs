using AppSupervisor.Core;
using AppSupervisor.Notifications;
using AppSupervisor.WindowsAudio;

namespace AppSupervisor.Resources;

/// <summary>Applies Windows endpoint volume and mute state and optionally restores the prior state.</summary>
internal sealed class AudioInterfaceResource :
    IManagedResource,
    IManagedResourceReadiness,
    IManagedResourceDeactivationState,
    IRecoverableResourceErrorSource
{
    private readonly AudioInterfaceResourceConfig _configuration;
    private readonly IWindowsAudioController _controller;
    private readonly TimeProvider _timeProvider;
    private readonly AutomaticRecoveryBudget _applyBudget = new();
    private readonly AutomaticRecoveryBudget _restoreBudget = new();
    private AudioInterfaceResourceConfig? _activatedEndpointIdentity;
    private AudioEndpointState? _originalState;
    private bool _profileActive;
    private bool _started;
    private bool _restorePending;
    private bool _errorActive;
    private DateTime _nextAttemptUtc;
    private bool _disposed;

    internal AudioInterfaceResource(AudioInterfaceResourceConfig configuration)
        : this(configuration, new WindowsAudioController(), SupervisorTime.Provider)
    {
    }

    internal AudioInterfaceResource(
        AudioInterfaceResourceConfig configuration,
        IWindowsAudioController controller,
        TimeProvider? timeProvider = null)
    {
        _configuration = configuration;
        _controller = controller;
        _timeProvider = timeProvider ?? SupervisorTime.Provider;
    }

    public event Action<IManagedResource, string>? ErrorOccurred;

    public event Action<IManagedResource>? ErrorCleared;

    public string DisplayName => GetDisplayName(_configuration);

    public IReadOnlyList<NotificationTarget> NotificationTargets =>
        _configuration.Notifications.Target;

    public bool DeactivationPending => !_disposed && !_profileActive && _restorePending;

    public void Activate()
    {
        if (_disposed)
            return;

        _profileActive = true;
        _applyBudget.Reset();
        _restoreBudget.Reset();
        _started = false;
        _restorePending = false;
        _activatedEndpointIdentity = null;
        _originalState = null;
        TryApplyConfiguredState();
    }

    public bool IsStarted() => !_disposed && _profileActive && _started;

    public ManagedResourceUpdate Supervise()
    {
        if (!_disposed && _profileActive && !_started && UtcNow >= _nextAttemptUtc)
            TryApplyConfiguredState();

        return ManagedResourceUpdate.None;
    }

    public void CancelPendingRecovery()
    {
        _applyBudget.Reset();
    }

    public void SuspendMonitoring()
    {
    }

    public void Deactivate()
    {
        if (_disposed)
            return;

        _profileActive = false;
        _started = false;
        _applyBudget.Reset();
        _restoreBudget.Reset();
        _restorePending = _configuration.RestoreOnDeactivate && _originalState is not null;

        if (_restorePending)
            TryRestoreOriginalState();
        else
        {
            _activatedEndpointIdentity = null;
            _originalState = null;
        }
    }

    public void SuperviseDeactivation()
    {
        if (!_disposed && _restorePending && UtcNow >= _nextAttemptUtc)
            TryRestoreOriginalState();
    }

    public void Dispose()
    {
        _disposed = true;
        _profileActive = false;
        _started = false;
        _restorePending = false;
        _activatedEndpointIdentity = null;
        _originalState = null;
        ErrorOccurred = null;
        ErrorCleared = null;
    }

    internal static string GetDisplayName(AudioInterfaceResourceConfig configuration)
    {
        if (configuration.UseDefaultDevice)
        {
            return configuration.Direction == AudioInterfaceDirection.Output
                ? "Default Windows audio output"
                : "Default Windows audio input";
        }

        string name = string.IsNullOrWhiteSpace(configuration.FriendlyName)
            ? "Windows audio interface"
            : configuration.FriendlyName;
        string direction = configuration.Direction == AudioInterfaceDirection.Output
            ? "output"
            : "input";
        return $"{name} ({direction})";
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    private void TryApplyConfiguredState()
    {
        DateTime nowUtc = UtcNow;

        if (!_applyBudget.TryBeginAttempt(nowUtc))
            return;

        try
        {
            AudioEndpointSnapshot endpoint = _controller.ResolveEndpoint(
                _activatedEndpointIdentity ?? _configuration
            );
            _activatedEndpointIdentity ??= CreatePhysicalIdentity(endpoint);
            _originalState ??= _controller.GetState(endpoint.EndpointId);
            _controller.SetState(
                endpoint.EndpointId,
                new AudioEndpointState(_configuration.VolumePercent / 100f, _configuration.Muted)
            );
            _started = true;
            _applyBudget.Reset();
            ClearError();
        }
        catch (Exception ex)
        {
            _started = false;
            _applyBudget.RecordFailure(nowUtc);
            _nextAttemptUtc = _applyBudget.NextAttemptUtc;
            ReportError(_applyBudget.DescribeFailure(
                $"Could not set {DisplayName}: {ex.Message}"
            ));
        }
    }

    private void TryRestoreOriginalState()
    {
        DateTime nowUtc = UtcNow;

        if (!_restoreBudget.TryBeginAttempt(nowUtc))
            return;

        try
        {
            AudioEndpointSnapshot endpoint = _controller.ResolveEndpoint(
                _activatedEndpointIdentity ?? _configuration
            );
            _controller.SetState(endpoint.EndpointId, _originalState!.Value);

            if (!_configuration.UseDefaultDevice)
                endpoint.CopyIdentityTo(_configuration);

            _restorePending = false;
            _activatedEndpointIdentity = null;
            _originalState = null;
            _restoreBudget.Reset();
            ClearError();
        }
        catch (Exception ex)
        {
            _restorePending = true;
            _restoreBudget.RecordFailure(nowUtc);
            _nextAttemptUtc = _restoreBudget.NextAttemptUtc;
            if (_restoreBudget.Exhausted)
                _restorePending = false;
            ReportError(_restoreBudget.DescribeFailure(
                $"Could not restore {DisplayName}: {ex.Message}"
            ));
        }
    }

    private void ReportError(string message)
    {
        _errorActive = true;
        ErrorOccurred?.Invoke(this, message);
    }

    private void ClearError()
    {
        if (!_errorActive)
            return;

        _errorActive = false;
        ErrorCleared?.Invoke(this);
    }

    private static AudioInterfaceResourceConfig CreatePhysicalIdentity(
        AudioEndpointSnapshot endpoint) => new()
    {
        EndpointId = endpoint.EndpointId,
        DeviceInstanceId = endpoint.DeviceInstanceId,
        ContainerId = endpoint.ContainerId,
        FriendlyName = endpoint.FriendlyName,
        InterfaceName = endpoint.InterfaceName,
        Direction = endpoint.Direction,
        UseDefaultDevice = false
    };
}
