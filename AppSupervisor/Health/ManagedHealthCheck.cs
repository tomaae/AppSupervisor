using AppSupervisor.Notifications;

namespace AppSupervisor.Health;

/// <summary>
/// Runs one non-overlapping asynchronous probe and applies startup delay, failure confirmation, and immediate recovery.
/// </summary>
public sealed class ManagedHealthCheck : IDisposable
{
    private readonly HealthCheckConfig _config;
    private readonly IHealthProbe _probe;
    private readonly IHealthCheckActivationCondition _activationCondition;

    private CancellationTokenSource? _probeCancellation;
    private Task<HealthProbeResult>? _probeTask;
    private DateTime? _activeSinceUtc;
    private DateTime _nextProbeUtc;
    private int _consecutiveFailures;
    private bool _unhealthy;
    private bool _recoveryConfirmationPending;
    private bool _hasResult;
    private string _lastDetail = "";
    private bool _discardProbeResult;
    private bool _resetProbeWhenCompleted;
    private bool _disposed;

    /// <summary>Creates a health-check state machine around one probe and prerequisite condition.</summary>
    /// <param name="config">The validated check settings.</param>
    /// <param name="probe">The health signal probe.</param>
    /// <param name="activationCondition">The external prerequisite that gates the check.</param>
    public ManagedHealthCheck(
        HealthCheckConfig config,
        IHealthProbe probe,
        IHealthCheckActivationCondition activationCondition)
    {
        _config = config;
        _probe = probe;
        _activationCondition = activationCondition;
    }

    /// <summary>Occurs once when the configured failure threshold is crossed.</summary>
    public event Action<ManagedHealthCheck, string>? Failed;

    /// <summary>Occurs when the first successful probe clears a previously failed check.</summary>
    public event Action<ManagedHealthCheck, string>? Recovered;

    /// <summary>Gets the configured human-readable health-check name.</summary>
    public string Name => _config.Name;

    /// <summary>Gets whether this check requests helper restart after confirmed failure.</summary>
    public bool RestartOnFailure => _config.RestartOnFailure;

    /// <summary>Gets the presentation targets configured specifically for this check.</summary>
    public IReadOnlyList<NotificationTarget> NotificationTargets => _config.Notifications.Target;

    /// <summary>Gets whether this check is currently applicable to an active helper.</summary>
    internal bool ApiActive => !_disposed && _activeSinceUtc is not null;

    /// <summary>Gets the last timer-cached health state without running the probe.</summary>
    internal string ApiStatus => !ApiActive
        ? "inactive"
        : _unhealthy
            ? "unhealthy"
            : _hasResult
                ? "healthy"
                : "checking";

    /// <summary>Gets the detail returned by the last completed probe.</summary>
    internal string ApiDetail => _lastDetail;

    /// <summary>Gets whether a cancelled probe task still needs to relinquish its resources.</summary>
    internal bool PauseDrainPending => _probeTask is not null;

    /// <summary>Reaps a cancelled probe once it has actually stopped using probe state.</summary>
    internal void AdvancePauseDrain()
    {
        FinalizeDiscardedProbeIfCompleted();
    }

    /// <summary>
    /// Starts a fresh post-restart verification window without clearing the active unhealthy
    /// notification; a repeated confirmed failure may therefore request the next bounded restart.
    /// </summary>
    internal void RearmAfterRecoveryAttempt(DateTime nowUtc, TimeSpan minimumDelay)
    {
        Suspend(clearError: false);
        _unhealthy = false;
        _recoveryConfirmationPending = true;
        _activeSinceUtc = nowUtc;
        _nextProbeUtc = nowUtc + minimumDelay;
    }

    /// <summary>Advances completion processing and starts a new probe only when its interval has elapsed.</summary>
    /// <param name="ownerProcessIds">The identifiers of all currently matching helper processes.</param>
    /// <param name="nowUtc">The current UTC time supplied by the supervision tick.</param>
    public void Poll(IReadOnlySet<int> ownerProcessIds, DateTime nowUtc)
    {
        FinalizeDiscardedProbeIfCompleted();

        if (_disposed)
            return;

        if (!_activationCondition.IsActive())
        {
            Suspend(clearError: true);
            return;
        }

        _activeSinceUtc ??= nowUtc;

        if (_probeTask is not null)
        {
            if (!_probeTask.IsCompleted)
                return;
            if (_discardProbeResult)
            {
                FinalizeDiscardedProbeIfCompleted();
            }
            else
            {

                ProcessCompletedProbe(nowUtc);
            }
        }

        if (_probeTask is not null || nowUtc < _nextProbeUtc)
            return;

        StartProbe(ownerProcessIds, nowUtc);
    }

    /// <summary>Cancels pending work and clears debounce and retained probe state.</summary>
    /// <param name="clearError">Whether a previously reported unhealthy state should emit recovery-state clearing.</param>
    public void Suspend(bool clearError)
    {
        SupervisorLog.WriteTrace(
            $"Health check '{Name}': suspend requested; probePending={_probeTask is not null}."
        );
        CancelAndDiscardProbe();

        if (_probeTask is null)
            _probe.Reset();
        else
            _resetProbeWhenCompleted = true;

        _activeSinceUtc = null;
        _nextProbeUtc = DateTime.MinValue;
        _consecutiveFailures = 0;
        _hasResult = false;
        _lastDetail = "";

        if (clearError && (_unhealthy || _recoveryConfirmationPending))
            Recovered?.Invoke(this, "The check is no longer applicable.");

        if (clearError)
        {
            _unhealthy = false;
            _recoveryConfirmationPending = false;
        }

        SupervisorLog.WriteTrace(
            $"Health check '{Name}': suspend completed; probePending={_probeTask is not null}."
        );
    }

    /// <summary>Cancels the probe, releases retained resources, and removes event subscribers.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _probeCancellation?.Cancel();

        Task<HealthProbeResult>? pendingTask = _probeTask;
        CancellationTokenSource? pendingCancellation = _probeCancellation;
        _probeTask = null;
        _probeCancellation = null;

        if (pendingTask is null || pendingTask.IsCompleted)
        {
            pendingCancellation?.Dispose();
            _probe.Dispose();
        }
        else
        {
            _ = pendingTask.ContinueWith(
                _ =>
                {
                    pendingCancellation?.Dispose();
                    _probe.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
        }

        Failed = null;
        Recovered = null;
    }

    /// <summary>Starts one timeout-bounded asynchronous probe without blocking the supervision timer.</summary>
    /// <param name="ownerProcessIds">The current helper process identifiers.</param>
    /// <param name="nowUtc">The start time used to schedule the next attempt.</param>
    private void StartProbe(IReadOnlySet<int> ownerProcessIds, DateTime nowUtc)
    {
        _probeCancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(_config.TimeoutSeconds)
        );
        _probeTask = RunProbeSafelyAsync(
            new HashSet<int>(ownerProcessIds),
            _probeCancellation.Token
        );
        _nextProbeUtc = nowUtc + TimeSpan.FromSeconds(_config.IntervalSeconds);
    }

    /// <summary>Converts probe exceptions and timeouts into ordinary unhealthy results.</summary>
    /// <param name="ownerProcessIds">A stable snapshot of helper process identifiers.</param>
    /// <param name="cancellationToken">The timeout or lifecycle cancellation token.</param>
    /// <returns>A successful or failed result that never faults the shared timer.</returns>
    private async Task<HealthProbeResult> RunProbeSafelyAsync(
        IReadOnlySet<int> ownerProcessIds,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _probe.CheckAsync(ownerProcessIds, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return HealthProbeResult.Failure("The health check timed out.");
        }
        catch (Exception ex)
        {
            return HealthProbeResult.Failure(ex.Message);
        }
    }

    /// <summary>Applies one completed result to startup delay, failure confirmation, and immediate recovery.</summary>
    /// <param name="nowUtc">The completion-processing time.</param>
    private void ProcessCompletedProbe(DateTime nowUtc)
    {
        Task<HealthProbeResult> completedTask = _probeTask!;
        _probeTask = null;
        _probeCancellation?.Dispose();
        _probeCancellation = null;

        HealthProbeResult result = completedTask.GetAwaiter().GetResult();
        _hasResult = true;
        _lastDetail = result.Detail;
        bool withinStartupDelay = nowUtc - _activeSinceUtc <
            TimeSpan.FromSeconds(_config.StartupDelaySeconds);

        if (result.Healthy)
        {
            _consecutiveFailures = 0;

            if (_unhealthy || _recoveryConfirmationPending)
            {
                _unhealthy = false;
                _recoveryConfirmationPending = false;
                Recovered?.Invoke(this, result.Detail);
            }

            return;
        }

        if (withinStartupDelay)
        {
            _consecutiveFailures = 0;
            return;
        }

        _consecutiveFailures++;

        if (_unhealthy || _consecutiveFailures < _config.FailureThreshold)
            return;

        _unhealthy = true;
        Failed?.Invoke(this, result.Detail);
    }

    /// <summary>Cancels the current probe and marks its eventual result as lifecycle noise.</summary>
    private void CancelAndDiscardProbe()
    {
        if (_probeTask is null)
            return;

        _discardProbeResult = true;
        _probeCancellation?.Cancel();
        FinalizeDiscardedProbeIfCompleted();
    }

    /// <summary>Releases a cancelled probe only after its task has actually stopped using probe state.</summary>
    private void FinalizeDiscardedProbeIfCompleted()
    {
        if (_probeTask is null || !_discardProbeResult || !_probeTask.IsCompleted)
            return;

        _probeCancellation?.Dispose();
        _probeCancellation = null;
        _probeTask = null;
        _discardProbeResult = false;

        if (_resetProbeWhenCompleted)
        {
            _resetProbeWhenCompleted = false;
            _probe.Reset();
        }
    }
}
