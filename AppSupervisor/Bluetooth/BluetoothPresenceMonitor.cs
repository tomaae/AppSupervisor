namespace AppSupervisor.Bluetooth;

/// <summary>Continuously scans registered devices and exposes debounced cached presence state.</summary>
internal sealed class BluetoothPresenceMonitor : IBluetoothPresenceSource, IDisposable
{
    private readonly IReadOnlyDictionary<string, BluetoothDeviceConfig> _devices;
    private readonly IBluetoothDeviceScanner _scanner;
    private readonly TimeSpan _scanInterval;
    private readonly TimeSpan _presenceTimeout;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _stateLock = new();
    private readonly Dictionary<string, DateTime> _lastSeenUtc =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Task _monitorTask;
    private string? _lastFailureMessage;
    private bool _disposed;

    internal BluetoothPresenceMonitor(
        BluetoothIntegrationConfig configuration,
        IBluetoothDeviceScanner? scanner = null,
        TimeSpan? scanInterval = null,
        TimeSpan? presenceTimeout = null)
    {
        _devices = configuration.Devices.ToDictionary(
            device => device.DeviceId,
            StringComparer.OrdinalIgnoreCase
        );
        _scanner = scanner ?? new BluetoothDeviceScanner();
        _scanInterval = scanInterval ?? TimeSpan.FromSeconds(configuration.ScanIntervalSeconds);
        _presenceTimeout = presenceTimeout ??
            TimeSpan.FromSeconds(configuration.PresenceTimeoutSeconds);
        _monitorTask = _devices.Count == 0
            ? Task.CompletedTask
            : Task.Run(() => MonitorAsync(_cancellation.Token));
    }

    /// <inheritdoc />
    public bool IsPresent(string deviceId)
    {
        lock (_stateLock)
        {
            return _lastSeenUtc.TryGetValue(deviceId, out DateTime lastSeenUtc) &&
                DateTime.UtcNow - lastSeenUtc <= _presenceTimeout;
        }
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            DateTime cycleStartedUtc = DateTime.UtcNow;

            try
            {
                IReadOnlyList<BluetoothDeviceSnapshot> discovered =
                    await _scanner.DiscoverAsync(cancellationToken).ConfigureAwait(false);
                DateTime observedUtc = DateTime.UtcNow;

                if (_lastFailureMessage is not null)
                {
                    SupervisorLog.WriteInformation("Bluetooth presence discovery recovered.");
                    _lastFailureMessage = null;
                }

                lock (_stateLock)
                {
                    foreach (BluetoothDeviceConfig configured in _devices.Values)
                    {
                        bool observed = discovered.Any(device =>
                            device.Kind == configured.Kind &&
                            string.Equals(
                                device.Address,
                                configured.Address,
                                StringComparison.OrdinalIgnoreCase
                            ) &&
                            (device.IsPresent || device.IsConnected)
                        );

                        if (observed)
                            _lastSeenUtc[configured.DeviceId] = observedUtc;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                if (!string.Equals(
                    _lastFailureMessage,
                    exception.Message,
                    StringComparison.Ordinal))
                {
                    _lastFailureMessage = exception.Message;
                    SupervisorLog.WriteWarning(
                        $"Bluetooth presence discovery failed: {exception.Message}"
                    );
                }
            }

            TimeSpan remaining = _scanInterval - (DateTime.UtcNow - cycleStartedUtc);
            if (remaining <= TimeSpan.Zero)
                continue;

            try
            {
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>Stops discovery and releases the monitor without changing external Bluetooth state.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cancellation.Cancel();

        try
        {
            if (!_monitorTask.Wait(TimeSpan.FromSeconds(2)))
            {
                _ = _monitorTask.ContinueWith(
                    _ => _cancellation.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                );
                return;
            }
        }
        catch (AggregateException exception) when (
            exception.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
        }

        _cancellation.Dispose();
    }
}
