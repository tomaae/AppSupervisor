using System.Diagnostics;
using AppSupervisor.Configuration;

using AppSupervisor.Core;

using AppSupervisor.Notifications;

namespace AppSupervisor.Resources;

/// <summary>
/// Supervises an executable identified by its full path and manages its start, restart, close, and minimization behavior.
/// </summary>
public sealed class ManagedApplication : IManagedApplicationLifecycle, IRecoverableResourceErrorSource
{
    private const int GracefulCloseTimeoutSeconds = 10;
    private const int GracefulCloseRetrySeconds = 2;
    private const int ForceKillConfirmationTimeoutSeconds = 5;
    private const int StartConfirmationTimeoutSeconds = 30;

    private readonly TimeSpan _restartTimeout;
    private readonly Func<bool>? _shouldRemainRunning;
    private readonly Func<IReadOnlySet<int>>? _processIdProvider;
    private readonly Func<ProcessStartInfo, Process?> _processStarter;
    private readonly TimeProvider _timeProvider;
    private readonly AutomaticRecoveryBudget _recoveryBudget = new();
    private readonly StartupMacroExecutor _startupMacro;
    private readonly string _runtimePath;

    private CloseOperation? _closeOperation;
    private WindowMinimizeOperation? _minimizeOperation;
    private HashSet<int>? _failedMultipleProcessIds;
    private DateTime? _missingSince;
    private DateTime? _closeObservationStartedUtc;
    private DateTime? _startRequestedUtc;
    private bool _launchIssued;
    private bool _activeDemand;
    private bool _startWaitReported;
    private bool _reportPendingStartAsRestart;
    private bool _disposed;
    private bool _lifecycleErrorActive;
    private bool _startupMacroErrorActive;
    private bool _hasObservedRunningState;
    private bool _lastObservedRunning;

    /// <summary>
    /// Creates a managed application with fresh restart, close, and minimization state.
    /// </summary>
    /// <param name="config">The executable and supervision settings for the application.</param>
    /// <param name="restartTimeout">How long an unexpectedly missing application may remain absent before restart.</param>
    public ManagedApplication(
        ManagedApplicationConfig config,
        TimeSpan restartTimeout)
        : this(config, restartTimeout, shouldRemainRunning: null)
    {
    }

    /// <summary>
    /// Creates a managed application with an optional shared-profile close guard.
    /// </summary>
    /// <param name="config">The executable and supervision settings for the application.</param>
    /// <param name="restartTimeout">How long an unexpectedly missing application may remain absent before restart.</param>
    /// <param name="shouldRemainRunning">Returns whether another profile still needs this executable.</param>
    internal ManagedApplication(
        ManagedApplicationConfig config,
        TimeSpan restartTimeout,
        Func<bool>? shouldRemainRunning,
        Func<IReadOnlySet<int>>? processIdProvider = null,
        TimeProvider? timeProvider = null,
        Func<ProcessStartInfo, Process?>? processStarter = null)
    {
        Config = config;
        _restartTimeout = restartTimeout;
        _shouldRemainRunning = shouldRemainRunning;
        _processIdProvider = processIdProvider;
        _timeProvider = timeProvider ?? SupervisorTime.Provider;
        _processStarter = processStarter ?? Process.Start;
        _runtimePath = JavaLauncherDetector.ResolveRuntimePath(config.Path);
        _startupMacro = new StartupMacroExecutor(
            config.StartupMacros,
            GetRunningProcessIds,
            ReportStartupMacroError,
            succeeded =>
            {
                if (succeeded)
                    ClearStartupMacroError();
            }
        );
    }

    /// <summary>Occurs when the helper cannot complete a requested lifecycle operation.</summary>
    public event Action<IManagedResource, string>? ErrorOccurred;

    /// <summary>Occurs when ordinary process lifecycle supervision succeeds after an error.</summary>
    public event Action<IManagedResource>? ErrorCleared;

    /// <summary>Gets the executable identity, launch, lifecycle, and notification settings.</summary>
    public ManagedApplicationConfig Config { get; }

    /// <summary>Gets whether a requested helper close is still awaiting completion or fallback handling.</summary>
    bool IManagedApplicationLifecycle.CloseOperationPending =>
        ProcessPathSnapshot.IsClosePending(_runtimePath);

    /// <summary>Gets whether this instance owns process mutation or post-launch window work.</summary>
    bool IManagedResourceLifecycleWork.LifecycleWorkPending =>
        ProcessPathSnapshot.GetOwnedTransition(_runtimePath, this) is not null ||
        _minimizeOperation is not null ||
        _startupMacro.Pending;

    /// <summary>Gets the helper executable filename used in notifications.</summary>
    public string DisplayName => Path.GetFileName(Config.Path);

    /// <summary>Gets the exact persistent process path used for supervision.</summary>
    internal string RuntimePath => _runtimePath;

    /// <summary>
    /// Gets the presentation targets configured specifically for this helper application.
    /// </summary>
    public IReadOnlyList<NotificationTarget> NotificationTargets => Config.Notifications.Target;

    /// <summary>Gets whether the startup macro is currently advancing from cached lifecycle state.</summary>
    internal bool ApiMacroPending => _startupMacro.Pending;

    /// <summary>Gets whether the last startup macro run has an uncleared error.</summary>
    internal bool ApiMacroError => _startupMacroErrorActive;

    /// <summary>Gets the latest lifecycle state without performing process discovery.</summary>
    internal ConfigurationResourceRuntimeStatus CachedRuntimeStatus
    {
        get
        {
            ProcessLifecycleTransitionKind? transition =
                ProcessPathSnapshot.GetTransition(_runtimePath);

            if (transition == ProcessLifecycleTransitionKind.Close)
                return ConfigurationResourceRuntimeStatus.Stopping;

            if (transition == ProcessLifecycleTransitionKind.Start ||
                _minimizeOperation is not null ||
                _startupMacro.Pending)
            {
                return ConfigurationResourceRuntimeStatus.Starting;
            }

            if (!_hasObservedRunningState)
                return ConfigurationResourceRuntimeStatus.Unknown;

            return _lastObservedRunning
                ? ConfigurationResourceRuntimeStatus.Running
                : ConfigurationResourceRuntimeStatus.NotRunning;
        }
    }

    /// <summary>
    /// Rediscovers the application by full executable path.
    /// </summary>
    /// <returns><see langword="true"/> when at least one matching process is currently running.</returns>
    public bool IsRunning()
    {
        return GetRunningProcessIds().Count > 0;
    }

    /// <summary>Returns exact-path identifiers from the current central supervision snapshot.</summary>
    internal IReadOnlySet<int> GetRunningProcessIds()
    {
        IReadOnlySet<int> processIds = _processIdProvider?.Invoke() ??
            ProcessPathSnapshot.FindExactPathProcessIds(_runtimePath);
        RememberRunningState(processIds.Count > 0);
        return processIds;
    }

    /// <summary>Checks whether the helper process is started for dependency sequencing.</summary>
    /// <returns><see langword="true"/> when at least one matching helper process is running.</returns>
    public bool IsStarted() =>
        !ProcessPathSnapshot.HasTransition(_runtimePath) && IsRunning();

    /// <summary>
    /// Ensures one helper instance is available, normalizing multiple instances before starting a fresh one.
    /// </summary>
    public void Activate()
    {
        if (_disposed)
            return;

        if (!_activeDemand)
            _recoveryBudget.Reset();

        _activeDemand = true;
        _missingSince = null;
        _failedMultipleProcessIds = null;

        if (ProcessPathSnapshot.IsClosePending(_runtimePath))
        {
            RequestStart(reportAsRestart: false);
            return;
        }

        IReadOnlySet<int> processIds = GetRunningProcessIds();

        if (processIds.Count == 0)
        {
            RequestStart(reportAsRestart: false);
        }
        else if (CountIndependentInstances(processIds) > 1)
        {
            RequestCloseThenStart(reportAsRestart: false);
        }
        else
        {
            _startupMacro.Start();
        }
    }

    /// <summary>
    /// Normalizes duplicate instances, detects an unexpected exit, and restarts after the configured restart timeout.
    /// </summary>
    /// <returns><see cref="ManagedResourceUpdate.Restarted"/> only when this cycle starts a replacement instance.</returns>
    public ManagedResourceUpdate Supervise()
    {
        if (_disposed)
            return ManagedResourceUpdate.None;

        if (ProcessPathSnapshot.HasTransition(_runtimePath))
            return ManagedResourceUpdate.None;

        bool wasRunning = _hasObservedRunningState && _lastObservedRunning;
        IReadOnlySet<int> processIds = GetRunningProcessIds();

        if (CountIndependentInstances(processIds) > 1)
        {
            if (MatchesFailedProcessSet(processIds))
                return ManagedResourceUpdate.None;

            _failedMultipleProcessIds = null;
            RequestCloseThenStart(reportAsRestart: true);
            return ManagedResourceUpdate.None;
        }

        _failedMultipleProcessIds = null;

        if (processIds.Count > 0)
        {
            _missingSince = null;
            _recoveryBudget.Reset();
            ClearError();

            if (!wasRunning)
                _startupMacro.Start();

            return ManagedResourceUpdate.None;
        }

        if (!Config.Restart)
            return ManagedResourceUpdate.None;

        if (_missingSince is null)
        {
            _missingSince = UtcNow;
            return ManagedResourceUpdate.None;
        }

        if (_recoveryBudget.Exhausted || UtcNow - _missingSince < _restartTimeout)
            return ManagedResourceUpdate.None;

        _missingSince = UtcNow;
        RequestStart(reportAsRestart: true);
        return ManagedResourceUpdate.None;
    }

    /// <summary>
    /// Cancels unissued recovery demand while allowing an accepted close mutation to finish safely.
    /// </summary>
    public void CancelPendingRecovery()
    {
        SupervisorLog.WriteTrace(
            $"Application '{DisplayName}': cancelling queued restart and minimize state."
        );
        _activeDemand = false;
        _missingSince = null;
        _recoveryBudget.Reset();
        ProcessLifecycleTransitionKind? transition =
            ProcessPathSnapshot.GetOwnedTransition(_runtimePath, this);

        if (transition == ProcessLifecycleTransitionKind.Start && !_launchIssued)
        {
            CompleteOwnedTransition(succeeded: true);
        }
        else
        {
            ProcessPathSnapshot.CancelQueuedTransitions(
                _runtimePath,
                this,
                ProcessLifecycleTransitionKind.Start
            );
        }

        _failedMultipleProcessIds = null;
        CancelMinimizeAfterStart();
        _startupMacro.Cancel();
        SupervisorLog.WriteTrace(
            $"Application '{DisplayName}': queued restart and minimize state cancelled."
        );
    }

    /// <summary>
    /// Resets restart timing after all accepted lifecycle and minimization work has drained for pause.
    /// </summary>
    public void SuspendMonitoring()
    {
        _missingSince = null;
    }

    /// <summary>
    /// Begins gracefully closing every process whose executable path matches this helper.
    /// </summary>
    public void Deactivate()
    {
        if (_disposed)
            return;

        _activeDemand = false;
        _missingSince = null;
        _recoveryBudget.Reset();
        _failedMultipleProcessIds = null;
        CancelMinimizeAfterStart();

        if (ShouldRemainRunning())
        {
            if (ProcessPathSnapshot.IsClosePending(_runtimePath))
                RequestStart(reportAsRestart: false);
            else
                CancelCloseOperation();

            return;
        }

        if (ProcessPathSnapshot.HasTransition(_runtimePath))
        {
            ProcessPathSnapshot.RequestTransition(
                _runtimePath,
                this,
                ProcessLifecycleTransitionKind.Close
            );
            return;
        }

        ExactPathObservation observation = ProcessPathSnapshot.ObserveExactPath(_runtimePath);

        if (observation.ProcessIds.Length == 0 && observation.IsAuthoritative)
        {
            CancelCloseOperation();
            if (ProcessPathSnapshot.GetOwnedTransition(_runtimePath, this) ==
                ProcessLifecycleTransitionKind.Close)
            {
                CompleteOwnedTransition(succeeded: true);
            }
            ClearError();
            return;
        }

        if (observation.ProcessIds.Length == 0)
        {
            ProcessPathSnapshot.RequestTransition(
                _runtimePath,
                this,
                ProcessLifecycleTransitionKind.Close
            );
            return;
        }

        ProcessPathSnapshot.RequestTransition(
            _runtimePath,
            this,
            ProcessLifecycleTransitionKind.Close
        );
    }

    /// <summary>
    /// Checks whether a graceful profile-deactivation close has completed and advances its fallback attempts.
    /// </summary>
    public void SuperviseDeactivation()
    {
        if (_disposed)
            return;

        ((IManagedResourceLifecycleWork)this).AdvanceLifecycle(SupervisorTime.UtcNow);
    }

    /// <summary>
    /// Cancels resource-owned asynchronous work and removes event subscribers without touching external processes.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CancelCloseOperation();
        CancelMinimizeAfterStart();
        _startupMacro.Cancel();
        ProcessPathSnapshot.ReleaseOwner(this);
        ErrorOccurred = null;
        ErrorCleared = null;
    }

    /// <summary>Checks the shared-profile guard conservatively before sending or advancing close requests.</summary>
    /// <returns><see langword="true"/> when another profile still needs this executable.</returns>
    private bool ShouldRemainRunning()
    {
        if (Config.LeaveRunningAfterProfileStops)
            return true;

        try
        {
            return _shouldRemainRunning?.Invoke() == true;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Advances the path-scoped start or close transition and post-launch minimization.</summary>
    ManagedResourceUpdate IManagedResourceLifecycleWork.AdvanceLifecycle(DateTime nowUtc)
    {
        if (_disposed)
            return ManagedResourceUpdate.None;

        ManagedResourceUpdate update = ManagedResourceUpdate.None;
        ProcessLifecycleTransitionKind? transition =
            ProcessPathSnapshot.GetOwnedTransition(_runtimePath, this);

        if (transition == ProcessLifecycleTransitionKind.Start)
            update = AdvanceStartOperation(nowUtc);
        else if (transition == ProcessLifecycleTransitionKind.Close)
            AdvanceOwnedCloseOperation(nowUtc);

        AdvanceMinimizeAfterStart(nowUtc);
        _startupMacro.Advance(nowUtc);
        return update;
    }

    /// <summary>Queues one start; a pending close is completed before this request becomes active.</summary>
    private void RequestStart(bool reportAsRestart)
    {
        ProcessPathSnapshot.RequestTransition(
            _runtimePath,
            this,
            ProcessLifecycleTransitionKind.Start
        );

        if (ProcessPathSnapshot.GetOwnedTransition(_runtimePath, this) ==
            ProcessLifecycleTransitionKind.Start)
        {
            _reportPendingStartAsRestart |= reportAsRestart;
        }
    }

    /// <summary>Queues duplicate normalization followed by one replacement start.</summary>
    private void RequestCloseThenStart(bool reportAsRestart)
    {
        ProcessPathSnapshot.RequestTransition(
            _runtimePath,
            this,
            ProcessLifecycleTransitionKind.Close
        );
        ProcessPathSnapshot.RequestTransition(
            _runtimePath,
            this,
            ProcessLifecycleTransitionKind.Start
        );
        _reportPendingStartAsRestart |= reportAsRestart;
    }

    /// <summary>Freshly verifies absence, issues one launch, and confirms exact-path appearance.</summary>
    private ManagedResourceUpdate AdvanceStartOperation(DateTime nowUtc)
    {
        var processes = FindRunningProcesses(fresh: true, out bool authoritative);

        try
        {
            if (processes.Count > 0)
            {
                bool reportRestart = _reportPendingStartAsRestart;
                ResetStartOperation();
                CompleteOwnedTransition(succeeded: true);
                _missingSince = null;
                _recoveryBudget.Reset();
                ClearError();

                bool macroMinimizes = Config.StartupMacros.Any(action =>
                    action.Type == StartupMacroActionType.Minimize);

                if (Config.MinimizeAfterStart && !macroMinimizes)
                    StartMinimizeAfterStart(nowUtc);

                _startupMacro.Start();

                return reportRestart
                    ? ManagedResourceUpdate.Restarted
                    : ManagedResourceUpdate.None;
            }
        }
        finally
        {
            DisposeProcesses(processes);
        }

        _startRequestedUtc ??= nowUtc;

        if (!authoritative)
        {
            if (nowUtc - _startRequestedUtc >=
                TimeSpan.FromSeconds(StartConfirmationTimeoutSeconds))
            {
                if (_activeDemand)
                {
                    ReportStartWait(
                        $"AppSupervisor still cannot safely verify whether {DisplayName} is already running. " +
                        "No duplicate launch will be attempted; startup monitoring will continue."
                    );
                }
                else
                {
                    ReportError(
                        $"Could not safely verify whether {DisplayName} was already running. No launch was attempted."
                    );
                    ResetStartOperation();
                    CompleteOwnedTransition(succeeded: false);
                }
            }

            return ManagedResourceUpdate.None;
        }

        if (!_launchIssued)
        {
            if (!_recoveryBudget.TryBeginAttempt(nowUtc))
            {
                if (_recoveryBudget.Exhausted)
                {
                    ResetStartOperation();
                    CompleteOwnedTransition(succeeded: false);
                }

                return ManagedResourceUpdate.None;
            }

            try
            {
                ProcessStartInfo startInfo = ApplicationUri.CreateStartInfo(Config);
                using Process? startedProcess = _processStarter(startInfo);

                if (startedProcess is null && string.IsNullOrWhiteSpace(Config.AppUri))
                    throw new InvalidOperationException("Windows did not return a process for the start request.");

                _launchIssued = true;
                return ManagedResourceUpdate.None;
            }
            catch (Exception ex)
            {
                _recoveryBudget.RecordFailure(nowUtc);
                ReportError(_recoveryBudget.DescribeFailure(
                    $"Could not start {DisplayName}: {ex.Message}"
                ));
                ResetStartOperation();
                CompleteOwnedTransition(succeeded: false);
                return ManagedResourceUpdate.None;
            }
        }

        if (_startRequestedUtc is DateTime requestedUtc &&
            nowUtc - requestedUtc >= TimeSpan.FromSeconds(StartConfirmationTimeoutSeconds))
        {
            if (_activeDemand)
            {
                ReportStartWait(
                    $"{DisplayName} has not appeared yet after Windows accepted its launch. " +
                    "Startup monitoring will continue without issuing a duplicate launch."
                );
            }
            else
            {
                ReportError(
                    $"Could not confirm that {DisplayName} started within {StartConfirmationTimeoutSeconds} seconds."
                );
                ResetStartOperation();
                CompleteOwnedTransition(succeeded: false);
            }
        }

        return ManagedResourceUpdate.None;
    }

    /// <summary>Initializes a claimed close transition and then advances it.</summary>
    private void AdvanceOwnedCloseOperation(DateTime nowUtc)
    {
        _closeObservationStartedUtc ??= nowUtc;

        if (_closeOperation is null)
        {
            var processes = FindRunningProcesses(fresh: true, out bool authoritative);

            try
            {
                if (processes.Count == 0 && authoritative)
                {
                    CompleteOwnedTransition(succeeded: true);
                    ClearError();
                    return;
                }

                if (processes.Count == 0)
                {
                    if (nowUtc - _closeObservationStartedUtc >=
                        TimeSpan.FromSeconds(GracefulCloseTimeoutSeconds +
                            ForceKillConfirmationTimeoutSeconds))
                    {
                        FailUnverifiableClose();
                    }

                    return;
                }

                BeginCloseOperation(processes, nowUtc);
            }
            finally
            {
                DisposeProcesses(processes);
            }

            return;
        }

        AdvanceCloseOperation(nowUtc);
    }

    /// <summary>
    /// Initializes a close operation and sends the first graceful WM_CLOSE request to every matching process.
    /// </summary>
    /// <param name="processes">Every currently matching helper process.</param>
    private void BeginCloseOperation(
        IReadOnlyCollection<Process> processes,
        DateTime nowUtc)
    {
        CancelMinimizeAfterStart();
        _startupMacro.Cancel();
        CancelCloseOperation();

        _closeOperation = new CloseOperation
        {
            StartedUtc = nowUtc,
            LastGracefulAttemptUtc = nowUtc,
            GracefulAttemptCount = 1
        };

        RequestGracefulClose(processes, attemptNumber: 0);
    }

    /// <summary>
    /// Confirms close progress, retries graceful methods, optionally force-kills, and starts one replacement only after all matches are gone.
    /// </summary>
    /// <param name="nowUtc">The timestamp shared by this lifecycle pass.</param>
    private void AdvanceCloseOperation(DateTime nowUtc)
    {
        var operation = _closeOperation;

        if (operation is null)
            return;

        var processes = FindRunningProcesses(fresh: true, out bool authoritative);

        try
        {
            if (processes.Count == 0 && authoritative)
            {
                CancelCloseOperation();
                _failedMultipleProcessIds = null;
                CompleteOwnedTransition(succeeded: true);
                ClearError();
                return;
            }

            if (processes.Count == 0)
            {
                if (nowUtc - operation.StartedUtc >=
                    TimeSpan.FromSeconds(GracefulCloseTimeoutSeconds +
                        ForceKillConfirmationTimeoutSeconds))
                {
                    FailUnverifiableClose();
                }

                return;
            }

            if (operation.ForceKillAttempted)
            {
                if (nowUtc - operation.ForceKillAttemptedUtc >=
                    TimeSpan.FromSeconds(ForceKillConfirmationTimeoutSeconds))
                {
                    FailCloseOperation(processes, forceKillAttempted: true);
                }

                return;
            }

            if (nowUtc - operation.StartedUtc >=
                TimeSpan.FromSeconds(GracefulCloseTimeoutSeconds))
            {
                if (Config.ForceKillAfterCloseFailure)
                {
                    ForceKill(processes);
                    operation.ForceKillAttempted = true;
                    operation.ForceKillAttemptedUtc = nowUtc;
                }
                else
                {
                    FailCloseOperation(processes, forceKillAttempted: false);
                }

                return;
            }

            if (nowUtc - operation.LastGracefulAttemptUtc >=
                TimeSpan.FromSeconds(GracefulCloseRetrySeconds))
            {
                if (operation.GracefulAttemptCount >= 2 &&
                    operation.TrayExitTask is null)
                {
                    StartTrayExitFallback(processes, operation);
                }

                RequestGracefulClose(processes, operation.GracefulAttemptCount);
                operation.GracefulAttemptCount++;
                operation.LastGracefulAttemptUtc = nowUtc;
            }
        }
        finally
        {
            DisposeProcesses(processes);
        }
    }

    /// <summary>
    /// Uses visible-window WM_CLOSE and CloseMainWindow retries to request graceful process shutdown.
    /// </summary>
    /// <param name="processes">Every matching process that is still running.</param>
    /// <param name="attemptNumber">The zero-based graceful attempt number.</param>
    private static void RequestGracefulClose(
        IEnumerable<Process> processes,
        int attemptNumber)
    {
        foreach (var process in processes)
        {
            try
            {
                if (attemptNumber == 0)
                    PostCloseToOwnedWindows(process);
                else
                    process.CloseMainWindow();
            }
            catch
            {
                // A final error is reported only if the process remains after all attempts.
            }
        }
    }

    /// <summary>
    /// Posts WM_CLOSE to qualifying top-level windows owned by a process.
    /// </summary>
    /// <param name="process">The process whose windows should receive the close request.</param>
    private static void PostCloseToOwnedWindows(Process process)
    {
        uint processId = (uint)process.Id;

        NativeMethods.EnumWindows((hWnd, lParam) =>
        {
            uint windowThreadId = NativeMethods.GetWindowThreadProcessId(
                hWnd,
                out uint windowProcessId
            );

            if (IsOwnedCloseTarget(
                processId,
                windowThreadId,
                windowProcessId,
                NativeMethods.IsWindowVisible(hWnd)))
            {
                NativeMethods.PostMessage(
                    hWnd,
                    NativeMethods.WM_CLOSE,
                    IntPtr.Zero,
                    IntPtr.Zero
                );
            }

            return true;
        }, IntPtr.Zero);
    }

    /// <summary>Determines whether one enumerated window should receive the current graceful close attempt.</summary>
    /// <param name="targetProcessId">The helper process being closed.</param>
    /// <param name="windowThreadId">The identifier of the thread that owns the candidate window.</param>
    /// <param name="windowProcessId">The process identifier reported for the candidate window.</param>
    /// <param name="isVisible">Whether the candidate is currently visible.</param>
    /// <returns><see langword="true"/> when the owned window should receive WM_CLOSE.</returns>
    internal static bool IsOwnedCloseTarget(
        uint targetProcessId,
        uint windowThreadId,
        uint windowProcessId,
        bool isVisible)
    {
        return windowThreadId != 0 &&
            windowProcessId == targetProcessId &&
            isVisible;
    }

    /// <summary>
    /// Starts one cancellation-aware background tray Exit request for every still-running match.
    /// </summary>
    /// <param name="processes">The matching helper processes that may expose tray menus.</param>
    /// <param name="operation">The close operation that owns and cancels the background requests.</param>
    private static void StartTrayExitFallback(
        IEnumerable<Process> processes,
        CloseOperation operation)
    {
        int[] processIds = processes
            .Select(process => process.Id)
            .ToArray();
        CancellationToken cancellationToken = operation.Cancellation.Token;

        operation.TrayExitTask = Task.WhenAll(
            processIds.Select(processId =>
                Task.Run(
                    () => TrayExitCloser.TryRequestExitAsync(
                        processId,
                        cancellationToken),
                    cancellationToken)));
    }

    /// <summary>
    /// Cancels an in-progress close and prevents its background tray request from invoking a stale command.
    /// </summary>
    private void CancelCloseOperation()
    {
        CloseOperation? operation = _closeOperation;
        _closeOperation = null;
        _closeObservationStartedUtc = null;

        if (operation is null)
            return;

        operation.Cancellation.Cancel();
        operation.Cancellation.Dispose();
    }

    /// <summary>
    /// Force-kills matching processes only after graceful attempts fail and explicit configuration permits it.
    /// </summary>
    /// <param name="processes">The matching processes that remain after the graceful timeout.</param>
    private static void ForceKill(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            try
            {
                process.Kill();
            }
            catch
            {
                // Confirmation and user-visible failure reporting occur on later supervisor ticks.
            }
        }
    }

    /// <summary>
    /// Reports a close failure once and remembers a failed duplicate set to avoid retrying it every second.
    /// </summary>
    /// <param name="processes">The matching processes still running after all configured attempts.</param>
    /// <param name="forceKillAttempted">Whether an explicitly enabled force-kill attempt was made.</param>
    private void FailCloseOperation(
        IReadOnlyCollection<Process> processes,
        bool forceKillAttempted)
    {
        _failedMultipleProcessIds = processes.Select(process => process.Id).ToHashSet();

        string finalAction = forceKillAttempted
            ? "An explicitly enabled force-kill attempt also failed."
            : "No force kill was attempted.";

        ReportError(
            $"Could not close {DisplayName}; {processes.Count} matching process(es) are still running. {finalAction}"
        );

        CancelCloseOperation();
        CompleteOwnedTransition(succeeded: false);
    }

    /// <summary>Terminates a close safely when same-name candidates cannot be inspected authoritatively.</summary>
    private void FailUnverifiableClose()
    {
        ReportError(
            $"Could not safely verify whether {DisplayName} finished closing. No additional process action was attempted."
        );
        CancelCloseOperation();
        CompleteOwnedTransition(succeeded: false);
    }

    /// <summary>
    /// Determines whether the current duplicate process set is the same set that already exhausted close attempts.
    /// </summary>
    /// <param name="processes">The currently matching helper processes.</param>
    /// <returns><see langword="true"/> when the process identifiers exactly match the remembered failed set.</returns>
    private bool MatchesFailedProcessSet(IEnumerable<int> processIds)
    {
        return _failedMultipleProcessIds is not null &&
               _failedMultipleProcessIds.SetEquals(processIds);
    }

    /// <summary>
    /// Starts post-launch minimization work owned by the shared lifecycle timer.
    /// </summary>
    private void StartMinimizeAfterStart(DateTime nowUtc)
    {
        _minimizeOperation = new WindowMinimizeOperation(nowUtc);
    }

    /// <summary>
    /// Stops an in-progress post-launch minimization routine.
    /// </summary>
    private void CancelMinimizeAfterStart()
    {
        _minimizeOperation = null;
    }

    /// <summary>
    /// Advances one nonblocking post-launch minimization check.
    /// </summary>
    private void AdvanceMinimizeAfterStart(DateTime nowUtc)
    {
        WindowMinimizeOperation? operation = _minimizeOperation;
        if (operation is null)
            return;

        try
        {
            bool? result = operation.Advance(nowUtc, () =>
            {
                var processes = FindRunningProcesses(fresh: true);

                try
                {
                    return CountIndependentInstances(processes) == 1 &&
                        processes.Any(process => WindowMinimizeOperation.MinimizeProcessWindows(process.Id));
                }
                finally
                {
                    DisposeProcesses(processes);
                }
            });

            if (result is null)
                return;

            _minimizeOperation = null;
            if (result == false)
            {
                ReportError(
                    $"Could not keep {DisplayName} minimized within {WindowMinimizeOperation.TimeoutMilliseconds / 1000} seconds."
                );
            }
        }
        catch (Exception ex)
        {
            _minimizeOperation = null;
            ReportError($"Could not minimize {DisplayName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens and revalidates process wrappers from the shared full-path candidate snapshot.
    /// </summary>
    /// <returns>All inspectable matching processes; the caller must dispose each returned object.</returns>
    private List<Process> FindRunningProcesses(bool fresh)
    {
        return FindRunningProcesses(fresh, out _);
    }

    /// <summary>Opens exact matches and reports whether absence was safely observable.</summary>
    private List<Process> FindRunningProcesses(bool fresh, out bool authoritative)
    {
        string targetPath = _runtimePath;
        var matches = new List<Process>();

        ExactPathObservation observation = fresh
            ? ProcessPathSnapshot.ObserveExactPathFresh(targetPath)
            : ProcessPathSnapshot.ObserveExactPath(targetPath);
        authoritative = observation.IsAuthoritative;

        foreach (int processId in observation.ProcessIds)
        {
            bool keepProcess = false;
            Process? process = null;

            try
            {
                process = Process.GetProcessById(processId);
                string? processPath = process.MainModule?.FileName;

                if (processPath is not null &&
                    string.Equals(
                        Path.GetFullPath(processPath),
                        targetPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(process);
                    keepProcess = true;
                }
            }
            catch
            {
                authoritative = false;
            }
            finally
            {
                if (!keepProcess)
                    process?.Dispose();
            }
        }

        if (matches.Count > 0)
            RememberRunningState(running: true);
        else if (authoritative)
            RememberRunningState(running: false);

        return matches;
    }

    /// <summary>Stores a lifecycle observation for query-free configuration UI display.</summary>
    private void RememberRunningState(bool running)
    {
        _hasObservedRunningState = true;
        _lastObservedRunning = running;
    }

    /// <summary>Gets suspend-aware time for restart grace periods and retry scheduling.</summary>
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>Counts independent application roots while treating same-executable child processes as one instance.</summary>
    /// <param name="processes">Every process whose executable path matches the helper identity.</param>
    /// <returns>The number of independent process trees represented by the matches.</returns>
    private static int CountIndependentInstances(IReadOnlyCollection<Process> processes)
    {
        if (processes.Count <= 1)
            return processes.Count;

        int[] processIds = processes.Select(process => process.Id).ToArray();
        return ProcessPathSnapshot.FindIndependentRootProcessIds(processIds).Count;
    }

    /// <summary>Counts independent application roots directly from cached exact-path identifiers.</summary>
    private static int CountIndependentInstances(IReadOnlyCollection<int> processIds)
    {
        if (processIds.Count <= 1)
            return processIds.Count;

        return ProcessPathSnapshot.FindIndependentRootProcessIds(processIds).Count;
    }

    /// <summary>
    /// Disposes process wrappers after their identifiers or window handles are no longer needed.
    /// </summary>
    /// <param name="processes">The process wrappers to dispose.</param>
    private static void DisposeProcesses(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
            process.Dispose();
    }

    /// <summary>Clears per-owner start confirmation state without altering a promoted transition.</summary>
    private void ResetStartOperation()
    {
        _launchIssued = false;
        _startRequestedUtc = null;
        _startWaitReported = false;
        _reportPendingStartAsRestart = false;
    }

    /// <summary>Reports one delayed-start diagnostic while retaining the accepted start transition.</summary>
    private void ReportStartWait(string message)
    {
        if (_startWaitReported)
            return;

        _startWaitReported = true;
        ReportError(message);
    }

    /// <summary>Completes this instance's current central transition.</summary>
    private void CompleteOwnedTransition(bool succeeded)
    {
        ProcessPathSnapshot.CompleteTransition(_runtimePath, this, succeeded);
    }

    /// <summary>
    /// Raises a user-visible supervision error without throwing from the shared timer tick.
    /// </summary>
    /// <param name="message">The error message to forward to the tray application.</param>
    private void ReportError(string message)
    {
        _lifecycleErrorActive = true;
        ErrorOccurred?.Invoke(this, message);
    }

    /// <summary>Clears one active lifecycle error after the helper returns to a valid state.</summary>
    private void ClearError()
    {
        if (!_lifecycleErrorActive)
            return;

        _lifecycleErrorActive = false;

        if (!_startupMacroErrorActive)
            ErrorCleared?.Invoke(this);
    }

    /// <summary>Reports a macro-specific recoverable error through this helper's notification targets.</summary>
    private void ReportStartupMacroError(string message)
    {
        _startupMacroErrorActive = true;
        ErrorOccurred?.Invoke(this, message);
    }

    /// <summary>Clears only the macro error after a later complete macro run succeeds.</summary>
    private void ClearStartupMacroError()
    {
        if (!_startupMacroErrorActive)
            return;

        _startupMacroErrorActive = false;

        if (!_lifecycleErrorActive)
            ErrorCleared?.Invoke(this);
    }

    /// <summary>
    /// Stores the runtime state of an in-progress all-instance close operation.
    /// </summary>
    private sealed class CloseOperation
    {
        /// <summary>Gets or sets when graceful close supervision began.</summary>
        public DateTime StartedUtc { get; set; }

        /// <summary>Gets or sets when the most recent graceful close request was sent.</summary>
        public DateTime LastGracefulAttemptUtc { get; set; }

        /// <summary>Gets or sets how many graceful request rounds have been sent.</summary>
        public int GracefulAttemptCount { get; set; }

        /// <summary>Gets or sets whether an explicitly permitted force-kill request was sent.</summary>
        public bool ForceKillAttempted { get; set; }

        /// <summary>Gets or sets when the force-kill request was sent.</summary>
        public DateTime ForceKillAttemptedUtc { get; set; }

        /// <summary>Gets the cancellation source that prevents a stale background tray command.</summary>
        public CancellationTokenSource Cancellation { get; } = new();

        /// <summary>Gets or sets the one background tray Exit request batch started for this operation.</summary>
        public Task? TrayExitTask { get; set; }
    }
}
