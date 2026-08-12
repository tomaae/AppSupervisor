using AppSupervisor.Configuration;
using AppSupervisor.Notifications;

namespace AppSupervisor.SteamVr;

/// <summary>Runs non-overlapping SteamVR scans and manages confirmed, remindable, silenceable incidents.</summary>
internal sealed class SteamVrDeviceMonitor : IDisposable
{
    internal static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    internal const int FailureThreshold = 2;

    private readonly ISteamVrDeviceSource _source;
    private readonly Dictionary<string, DeviceState> _states =
        new(StringComparer.OrdinalIgnoreCase);
    private SteamVrIntegrationConfig _config = new();
    private Task<SteamVrSnapshot>? _pendingCapture;
    private DateTime _nextCheckUtc;
    private DateTime? _sessionStartedUtc;
    private string? _lastSourceError;
    private bool _disposed;

    public SteamVrDeviceMonitor(ISteamVrDeviceSource source)
    {
        _source = source;
    }

    /// <summary>Occurs whenever the offline-device list or acknowledgement state changes.</summary>
    public event Action<IReadOnlyList<SteamVrOfflineDevice>>? OfflineDevicesChanged;

    /// <summary>Occurs when a recovery or monitoring-source error needs presentation without an incident window.</summary>
    public event Action<SupervisorNotification>? NotificationRequested;

    /// <summary>Occurs when a targeted offline notification must be published before showing its alert window.</summary>
    public event Action<SupervisorNotification>? AlertRequested;

    /// <summary>Gets the current confirmed incidents, including silenced incidents.</summary>
    public IReadOnlyList<SteamVrOfflineDevice> OfflineDevices => CreateOfflineSnapshot();

    /// <summary>Gets whether at least one confirmed device incident remains unresolved.</summary>
    public bool HasOfflineDevices => _states.Values.Any(state => state.OfflineSinceUtc is not null);

    /// <summary>Gets whether monitoring is enabled by the current global configuration.</summary>
    public bool Enabled => _config.Enabled;

    /// <summary>Replaces global settings while preserving active incidents when monitoring remains enabled.</summary>
    public void ApplyConfiguration(SteamVrIntegrationConfig configuration)
    {
        SteamVrIntegrationConfig replacement = ConfigJson.Clone(configuration);
        bool wasEnabled = _config.Enabled;

        if (!replacement.Enabled)
        {
            _config = replacement;
            ResetSession(clearIncidents: true);
            _states.Clear();
            _pendingCapture = null;
            _nextCheckUtc = DateTime.MinValue;
            return;
        }

        _config = replacement;

        if (!wasEnabled)
        {
            ResetSession(clearIncidents: true);
            _states.Clear();
            _pendingCapture = null;
            _nextCheckUtc = DateTime.MinValue;
            return;
        }

        SynchronizeConfiguredStates();
        RaiseOfflineDevicesChanged();
    }

    /// <summary>Starts or finalizes at most one background scan when its 30-second interval is due.</summary>
    public void Advance(DateTime nowUtc)
    {
        if (_disposed || !_config.Enabled)
            return;

        FinalizeCapture(nowUtc);
        PublishDueReminders(nowUtc);

        if (_pendingCapture is not null || nowUtc < _nextCheckUtc)
            return;

        _pendingCapture = Task.Run(_source.Capture);
    }

    /// <summary>Captures a live snapshot for configuration discovery through the same serialized source.</summary>
    public Task<SteamVrSnapshot> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(_source.Capture, cancellationToken);
    }

    /// <summary>Silences reminder notifications for selected devices until they recover.</summary>
    public void Silence(IEnumerable<string> serialNumbers)
    {
        bool changed = false;

        foreach (string serialNumber in serialNumbers.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_states.TryGetValue(serialNumber, out DeviceState? state) &&
                state.OfflineSinceUtc is not null &&
                !state.Silenced)
            {
                state.Silenced = true;
                changed = true;
            }
        }

        if (changed)
            RaiseOfflineDevicesChanged();
    }

    /// <summary>Suspends global monitoring and clears transient failures and incidents.</summary>
    public void Suspend()
    {
        ResetSession(clearIncidents: true);
        _nextCheckUtc = DateTime.MinValue;
        _pendingCapture = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ResetSession(clearIncidents: true);
        _pendingCapture = null;
        _source.Dispose();
        OfflineDevicesChanged = null;
        NotificationRequested = null;
        AlertRequested = null;
    }

    private void FinalizeCapture(DateTime nowUtc)
    {
        if (_pendingCapture is null || !_pendingCapture.IsCompleted)
            return;

        Task<SteamVrSnapshot> completed = _pendingCapture;
        _pendingCapture = null;
        SteamVrSnapshot snapshot;

        try
        {
            snapshot = completed.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            snapshot = new SteamVrSnapshot(true, _sessionStartedUtc, [], ex.Message);
        }

        ProcessSnapshot(snapshot, nowUtc);

        if (_nextCheckUtc <= nowUtc)
            _nextCheckUtc = nowUtc + CheckInterval;
    }

    internal void ProcessSnapshot(SteamVrSnapshot snapshot, DateTime nowUtc)
    {
        if (!snapshot.SteamVrActive)
        {
            ResetSession(clearIncidents: true);
            return;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Error))
        {
            if (!string.Equals(_lastSourceError, snapshot.Error, StringComparison.Ordinal))
            {
                _lastSourceError = snapshot.Error;
                NotificationRequested?.Invoke(new SupervisorNotification(
                    NotificationSeverity.Warning,
                    "SteamVR monitoring unavailable",
                    snapshot.Error,
                    _config.Notifications.Target
                ));
            }

            return;
        }

        _lastSourceError = null;
        DateTime observedSessionStart = snapshot.SteamVrStartedUtc ?? nowUtc;

        if (_sessionStartedUtc != observedSessionStart)
        {
            ResetSession(clearIncidents: true);
            _sessionStartedUtc = observedSessionStart;
        }

        DateTime graceEndsUtc = observedSessionStart + StartupGrace;

        if (nowUtc < graceEndsUtc)
        {
            _nextCheckUtc = graceEndsUtc;
            return;
        }

        var connectedSerials = snapshot.Devices
            .Where(device => device.Connected)
            .Select(device => device.SerialNumber)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newlyOffline = new List<DeviceState>();
        var recovered = new List<DeviceState>();

        SynchronizeConfiguredStates();

        foreach (DeviceState state in _states.Values)
        {
            if (connectedSerials.Contains(state.Device.SerialNumber))
            {
                state.ConsecutiveFailures = 0;

                if (state.OfflineSinceUtc is not null)
                {
                    recovered.Add(state);
                    state.OfflineSinceUtc = null;
                    state.NextReminderUtc = null;
                    state.Silenced = false;
                }

                continue;
            }

            state.ConsecutiveFailures++;

            if (state.OfflineSinceUtc is null && state.ConsecutiveFailures >= FailureThreshold)
            {
                state.OfflineSinceUtc = nowUtc;
                state.NextReminderUtc = nowUtc + ReminderInterval;
                state.Silenced = false;
                newlyOffline.Add(state);
            }
        }

        if (newlyOffline.Count > 0)
            RequestOfflineAlert(newlyOffline, reminder: false);

        foreach (DeviceState state in recovered)
        {
            NotificationRequested?.Invoke(new SupervisorNotification(
                NotificationSeverity.Information,
                "SteamVR device recovered",
                $"{state.Device.Name} is connected again.",
                _config.Notifications.Target
            ));
        }

        if (newlyOffline.Count > 0 || recovered.Count > 0)
            RaiseOfflineDevicesChanged();

        PublishDueReminders(nowUtc);
    }

    /// <summary>Publishes reminders whose deadlines have elapsed independently of the slower device scan.</summary>
    /// <param name="nowUtc">The current supervisor tick time.</param>
    private void PublishDueReminders(DateTime nowUtc)
    {
        List<DeviceState> reminders = _states.Values
            .Where(state =>
                state.OfflineSinceUtc is not null &&
                !state.Silenced &&
                state.NextReminderUtc <= nowUtc)
            .ToList();

        if (reminders.Count == 0)
            return;

        foreach (DeviceState state in reminders)
            state.NextReminderUtc = nowUtc + ReminderInterval;

        RequestOfflineAlert(reminders, reminder: true);
    }

    private void SynchronizeConfiguredStates()
    {
        var enabledSerials = _config.Devices
            .Where(device => device.Enabled)
            .Select(device => device.SerialNumber)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string removed in _states.Keys.Where(serial => !enabledSerials.Contains(serial)).ToArray())
            _states.Remove(removed);

        foreach (SteamVrDeviceConfig device in _config.Devices.Where(device => device.Enabled))
        {
            if (_states.TryGetValue(device.SerialNumber, out DeviceState? state))
                state.Device = device;
            else
                _states.Add(device.SerialNumber, new DeviceState(device));
        }
    }

    /// <summary>Raises one inseparable offline notification and alert-window request.</summary>
    /// <param name="devices">The newly offline devices or devices due for a reminder.</param>
    /// <param name="reminder">Whether this is a repeated reminder for an existing incident.</param>
    private void RequestOfflineAlert(IReadOnlyCollection<DeviceState> devices, bool reminder)
    {
        string names = string.Join(
            Environment.NewLine,
            devices.Select(state => $"• {state.Device.Name}")
        );
        AlertRequested?.Invoke(new SupervisorNotification(
            NotificationSeverity.Error,
            reminder ? "SteamVR devices still offline" : "SteamVR device offline",
            names,
            _config.Notifications.Target
        ));
    }

    private TimeSpan ReminderInterval => TimeSpan.FromMinutes(_config.ReminderIntervalMinutes);

    private void ResetSession(bool clearIncidents)
    {
        bool hadIncidents = HasOfflineDevices;
        _sessionStartedUtc = null;
        _lastSourceError = null;

        foreach (DeviceState state in _states.Values)
        {
            state.ConsecutiveFailures = 0;

            if (clearIncidents)
            {
                state.OfflineSinceUtc = null;
                state.NextReminderUtc = null;
                state.Silenced = false;
            }
        }

        if (clearIncidents && hadIncidents)
            RaiseOfflineDevicesChanged();
    }

    private IReadOnlyList<SteamVrOfflineDevice> CreateOfflineSnapshot()
    {
        return _states.Values
            .Where(state => state.OfflineSinceUtc is not null)
            .Select(state => new SteamVrOfflineDevice(
                state.Device.SerialNumber,
                state.Device.Name,
                state.Device.DeviceClass,
                state.OfflineSinceUtc!.Value,
                state.Silenced
            ))
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void RaiseOfflineDevicesChanged()
        => OfflineDevicesChanged?.Invoke(CreateOfflineSnapshot());

    private sealed class DeviceState
    {
        public DeviceState(SteamVrDeviceConfig device)
        {
            Device = device;
        }

        public SteamVrDeviceConfig Device { get; set; }
        public int ConsecutiveFailures { get; set; }
        public DateTime? OfflineSinceUtc { get; set; }
        public DateTime? NextReminderUtc { get; set; }
        public bool Silenced { get; set; }
    }
}

/// <summary>Describes one confirmed SteamVR incident presented by the modeless alert window.</summary>
internal sealed record SteamVrOfflineDevice(
    string SerialNumber,
    string Name,
    SteamVrDeviceClass DeviceClass,
    DateTime OfflineSinceUtc,
    bool Silenced);
