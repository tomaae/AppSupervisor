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

    private readonly TimeSpan _restartTimeout;
    private readonly Func<bool>? _shouldRemainRunning;

    private CancellationTokenSource? _minimizeCancellation;
    private CloseOperation? _closeOperation;
    private HashSet<int>? _failedMultipleProcessIds;
    private DateTime? _missingSince;
    private bool _disposed;
    private bool _errorActive;

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
        Func<bool>? shouldRemainRunning)
    {
        Config = config;
        _restartTimeout = restartTimeout;
        _shouldRemainRunning = shouldRemainRunning;
    }

    /// <summary>Occurs when the helper cannot complete a requested lifecycle operation.</summary>
    public event Action<IManagedResource, string>? ErrorOccurred;

    /// <summary>Occurs when ordinary process lifecycle supervision succeeds after an error.</summary>
    public event Action<IManagedResource>? ErrorCleared;

    /// <summary>Gets the executable identity, launch, lifecycle, and notification settings.</summary>
    public ManagedApplicationConfig Config { get; }

    /// <summary>Gets whether a requested helper close is still awaiting completion or fallback handling.</summary>
    bool IManagedApplicationLifecycle.CloseOperationPending => _closeOperation is not null;

    /// <summary>Gets the helper executable filename used in notifications.</summary>
    public string DisplayName => Path.GetFileName(Config.Path);

    /// <summary>
    /// Gets the presentation targets configured specifically for this helper application.
    /// </summary>
    public IReadOnlyList<NotificationTarget> NotificationTargets => Config.Notifications.Target;

    /// <summary>
    /// Rediscovers the application by full executable path.
    /// </summary>
    /// <returns><see langword="true"/> when at least one matching process is currently running.</returns>
    public bool IsRunning()
    {
        var processes = FindRunningProcesses();

        try
        {
            return processes.Count > 0;
        }
        finally
        {
            DisposeProcesses(processes);
        }
    }

    /// <summary>Checks whether the helper process is started for dependency sequencing.</summary>
    /// <returns><see langword="true"/> when at least one matching helper process is running.</returns>
    public bool IsStarted() => IsRunning();

    /// <summary>
    /// Ensures one helper instance is available, normalizing multiple instances before starting a fresh one.
    /// </summary>
    public void Activate()
    {
        if (_disposed)
            return;

        _missingSince = null;
        _failedMultipleProcessIds = null;

        if (_closeOperation is not null)
        {
            _closeOperation.RestartAfterClose = true;
            return;
        }

        var processes = FindRunningProcesses();

        try
        {
            if (processes.Count == 0)
            {
                TryStart();
            }
            else if (processes.Count > 1)
            {
                BeginCloseOperation(processes, restartAfterClose: true);
            }
        }
        finally
        {
            DisposeProcesses(processes);
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

        if (_closeOperation is not null)
            return AdvanceCloseOperation(profileActive: true);

        var processes = FindRunningProcesses();

        try
        {
            if (processes.Count > 1)
            {
                if (MatchesFailedProcessSet(processes))
                    return ManagedResourceUpdate.None;

                _failedMultipleProcessIds = null;
                BeginCloseOperation(processes, restartAfterClose: true);
                return ManagedResourceUpdate.None;
            }

            _failedMultipleProcessIds = null;

            if (processes.Count == 1)
            {
                _missingSince = null;
                ClearError();
                return ManagedResourceUpdate.None;
            }
        }
        finally
        {
            DisposeProcesses(processes);
        }

        if (!Config.Restart)
            return ManagedResourceUpdate.None;

        if (_missingSince is null)
        {
            _missingSince = DateTime.UtcNow;
            return ManagedResourceUpdate.None;
        }

        if (DateTime.UtcNow - _missingSince < _restartTimeout)
            return ManagedResourceUpdate.None;

        _missingSince = DateTime.UtcNow;

        return TryStart()
            ? ManagedResourceUpdate.Restarted
            : ManagedResourceUpdate.None;
    }

    /// <summary>
    /// Cancels restart, duplicate-normalization, and minimization work as soon as the monitoring trigger disappears.
    /// </summary>
    public void CancelPendingRecovery()
    {
        _missingSince = null;
        _closeOperation = null;
        _failedMultipleProcessIds = null;
        CancelMinimizeAfterStart();
    }

    /// <summary>
    /// Cancels minimization and resets restart timing while preserving any close request already sent before supervision paused.
    /// </summary>
    public void SuspendMonitoring()
    {
        _missingSince = null;
        CancelMinimizeAfterStart();
    }

    /// <summary>
    /// Begins gracefully closing every process whose executable path matches this helper.
    /// </summary>
    public void Deactivate()
    {
        if (_disposed)
            return;

        _missingSince = null;
        _failedMultipleProcessIds = null;
        CancelMinimizeAfterStart();

        if (ShouldRemainRunning())
        {
            _closeOperation = null;
            return;
        }

        var processes = FindRunningProcesses();

        try
        {
            if (processes.Count == 0)
            {
                _closeOperation = null;
                ClearError();
                return;
            }

            BeginCloseOperation(processes, restartAfterClose: false);
        }
        finally
        {
            DisposeProcesses(processes);
        }
    }

    /// <summary>
    /// Checks whether a graceful profile-deactivation close has completed and advances its fallback attempts.
    /// </summary>
    public void SuperviseDeactivation()
    {
        if (_disposed || _closeOperation is null)
            return;

        if (ShouldRemainRunning())
        {
            _closeOperation = null;
            return;
        }

        AdvanceCloseOperation(profileActive: false);
    }

    /// <summary>
    /// Cancels resource-owned asynchronous work and removes event subscribers without touching external processes.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _closeOperation = null;
        CancelMinimizeAfterStart();
        ErrorOccurred = null;
        ErrorCleared = null;
    }

    /// <summary>Checks the shared-profile guard conservatively before sending or advancing close requests.</summary>
    /// <returns><see langword="true"/> when another profile still needs this executable.</returns>
    private bool ShouldRemainRunning()
    {
        try
        {
            return _shouldRemainRunning?.Invoke() == true;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Starts the configured executable and reports launch failures without allowing them to escape the supervisor tick.
    /// </summary>
    /// <returns><see langword="true"/> when a new process was started successfully.</returns>
    private bool TryStart()
    {
        var existingProcesses = FindRunningProcesses();

        try
        {
            if (existingProcesses.Count > 0)
                return false;
        }
        finally
        {
            DisposeProcesses(existingProcesses);
        }

        try
        {
            ProcessStartInfo startInfo = ApplicationUri.CreateStartInfo(Config);

            using Process? startedProcess = Process.Start(startInfo);

            if (startedProcess is null && string.IsNullOrWhiteSpace(Config.AppUri))
                throw new InvalidOperationException("Windows did not return a process for the start request.");

            _missingSince = null;
            ClearError();

            if (Config.MinimizeAfterStart)
                StartMinimizeAfterStart();

            return true;
        }
        catch (Exception ex)
        {
            ReportError($"Could not start {DisplayName}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Initializes a close operation and sends the first graceful WM_CLOSE request to every matching process.
    /// </summary>
    /// <param name="processes">Every currently matching helper process.</param>
    /// <param name="restartAfterClose">Whether one fresh instance should start after all matches are confirmed gone.</param>
    private void BeginCloseOperation(
        IReadOnlyCollection<Process> processes,
        bool restartAfterClose)
    {
        CancelMinimizeAfterStart();

        var now = DateTime.UtcNow;
        _closeOperation = new CloseOperation
        {
            RestartAfterClose = restartAfterClose,
            StartedUtc = now,
            LastGracefulAttemptUtc = now,
            GracefulAttemptCount = 1
        };

        RequestGracefulClose(processes, attemptNumber: 0);
    }

    /// <summary>
    /// Confirms close progress, retries graceful methods, optionally force-kills, and starts one replacement only after all matches are gone.
    /// </summary>
    /// <param name="profileActive">Whether starting a replacement remains allowed.</param>
    /// <returns><see cref="ManagedResourceUpdate.Restarted"/> when a fresh replacement was started.</returns>
    private ManagedResourceUpdate AdvanceCloseOperation(bool profileActive)
    {
        var operation = _closeOperation;

        if (operation is null)
            return ManagedResourceUpdate.None;

        var processes = FindRunningProcesses();

        try
        {
            if (processes.Count == 0)
            {
                bool restart = profileActive && operation.RestartAfterClose;
                _closeOperation = null;
                _failedMultipleProcessIds = null;

                return restart && TryStart()
                    ? ManagedResourceUpdate.Restarted
                    : ManagedResourceUpdate.None;
            }

            DateTime now = DateTime.UtcNow;

            if (operation.ForceKillAttempted)
            {
                if (now - operation.ForceKillAttemptedUtc >=
                    TimeSpan.FromSeconds(ForceKillConfirmationTimeoutSeconds))
                {
                    FailCloseOperation(processes, forceKillAttempted: true);
                }

                return ManagedResourceUpdate.None;
            }

            if (now - operation.StartedUtc >=
                TimeSpan.FromSeconds(GracefulCloseTimeoutSeconds))
            {
                if (Config.ForceKillAfterCloseFailure)
                {
                    ForceKill(processes);
                    operation.ForceKillAttempted = true;
                    operation.ForceKillAttemptedUtc = now;
                }
                else
                {
                    FailCloseOperation(processes, forceKillAttempted: false);
                }

                return ManagedResourceUpdate.None;
            }

            if (now - operation.LastGracefulAttemptUtc >=
                TimeSpan.FromSeconds(GracefulCloseRetrySeconds))
            {
                RequestGracefulClose(processes, operation.GracefulAttemptCount);
                operation.GracefulAttemptCount++;
                operation.LastGracefulAttemptUtc = now;
            }

            return ManagedResourceUpdate.None;
        }
        finally
        {
            DisposeProcesses(processes);
        }
    }

    /// <summary>
    /// Uses WM_CLOSE, CloseMainWindow, and later combined retries to request graceful process shutdown.
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
                if (attemptNumber == 0 || attemptNumber >= 2)
                    PostCloseToVisibleWindows(process);

                if (attemptNumber >= 1)
                    process.CloseMainWindow();
            }
            catch
            {
                // A final error is reported only if the process remains after all attempts.
            }
        }
    }

    /// <summary>
    /// Posts WM_CLOSE to every visible top-level window owned by a process.
    /// </summary>
    /// <param name="process">The process whose windows should receive the close request.</param>
    private static void PostCloseToVisibleWindows(Process process)
    {
        uint processId = (uint)process.Id;

        NativeMethods.EnumWindows((hWnd, lParam) =>
        {
            uint windowThreadId = NativeMethods.GetWindowThreadProcessId(
                hWnd,
                out uint windowProcessId
            );

            if (windowThreadId != 0 &&
                windowProcessId == processId &&
                NativeMethods.IsWindowVisible(hWnd))
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
        var operation = _closeOperation;

        if (operation?.RestartAfterClose == true)
            _failedMultipleProcessIds = processes.Select(process => process.Id).ToHashSet();

        string finalAction = forceKillAttempted
            ? "An explicitly enabled force-kill attempt also failed."
            : "No force kill was attempted.";

        ReportError(
            $"Could not close {DisplayName}; {processes.Count} matching process(es) are still running. {finalAction}"
        );

        _closeOperation = null;
    }

    /// <summary>
    /// Determines whether the current duplicate process set is the same set that already exhausted close attempts.
    /// </summary>
    /// <param name="processes">The currently matching helper processes.</param>
    /// <returns><see langword="true"/> when the process identifiers exactly match the remembered failed set.</returns>
    private bool MatchesFailedProcessSet(IReadOnlyCollection<Process> processes)
    {
        return _failedMultipleProcessIds is not null &&
               _failedMultipleProcessIds.SetEquals(processes.Select(process => process.Id));
    }

    /// <summary>
    /// Cancels any previous minimization routine and starts a new cancellable routine for the launched helper.
    /// </summary>
    private void StartMinimizeAfterStart()
    {
        CancelMinimizeAfterStart();
        _minimizeCancellation = new CancellationTokenSource();
        _ = MinimizeAfterStartAsync(_minimizeCancellation.Token);
    }

    /// <summary>
    /// Stops an in-progress post-launch minimization routine.
    /// </summary>
    private void CancelMinimizeAfterStart()
    {
        if (_minimizeCancellation is null)
            return;

        _minimizeCancellation.Cancel();
        _minimizeCancellation.Dispose();
        _minimizeCancellation = null;
    }

    /// <summary>
    /// Minimizes all visible top-level windows that belong to the supplied process.
    /// </summary>
    /// <param name="process">The process whose windows should be minimized.</param>
    /// <returns><see langword="true"/> when at least one matching window is minimized.</returns>
    private static bool MinimizeProcessWindows(Process process)
    {
        uint processId = (uint)process.Id;
        bool minimized = false;

        NativeMethods.EnumWindows((hWnd, lParam) =>
        {
            uint windowThreadId = NativeMethods.GetWindowThreadProcessId(
                hWnd,
                out uint windowProcessId
            );

            if (windowThreadId == 0 ||
                windowProcessId != processId ||
                !NativeMethods.IsWindowVisible(hWnd))
                return true;

            if (NativeMethods.IsIconic(hWnd))
            {
                minimized = true;
                return true;
            }

            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_MINIMIZE);

            if (NativeMethods.IsIconic(hWnd))
                minimized = true;

            return true;
        }, IntPtr.Zero);

        return minimized;
    }

    /// <summary>
    /// Repeatedly minimizes a newly started application until stable, timed out, or cancelled by profile inactivity or disposal.
    /// </summary>
    /// <param name="cancellationToken">Stops window manipulation when the profile or configuration becomes inactive.</param>
    private async Task MinimizeAfterStartAsync(CancellationToken cancellationToken)
    {
        const int timeoutMilliseconds = 10000;
        const int checkIntervalMilliseconds = 250;
        const int stableMillisecondsRequired = 1000;

        int elapsed = 0;
        int minimizedStableFor = 0;

        try
        {
            while (elapsed < timeoutMilliseconds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var processes = FindRunningProcesses();

                try
                {
                    if (processes.Count == 1)
                    {
                        bool minimized = MinimizeProcessWindows(processes[0]);

                        if (minimized)
                        {
                            minimizedStableFor += checkIntervalMilliseconds;

                            if (minimizedStableFor >= stableMillisecondsRequired)
                                return;
                        }
                        else
                        {
                            minimizedStableFor = 0;
                        }
                    }
                    else
                    {
                        minimizedStableFor = 0;
                    }
                }
                finally
                {
                    DisposeProcesses(processes);
                }

                await Task.Delay(checkIntervalMilliseconds, cancellationToken);
                elapsed += checkIntervalMilliseconds;
            }

            ReportError(
                $"Could not keep {DisplayName} minimized within {timeoutMilliseconds / 1000} seconds."
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is expected when the profile becomes inactive, reloads, or exits.
        }
        catch (Exception ex)
        {
            ReportError($"Could not minimize {DisplayName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens and revalidates process wrappers from the shared full-path candidate snapshot.
    /// </summary>
    /// <returns>All inspectable matching processes; the caller must dispose each returned object.</returns>
    private List<Process> FindRunningProcesses()
    {
        string targetPath = Path.GetFullPath(Config.Path);
        var matches = new List<Process>();

        foreach (int processId in ProcessPathSnapshot.FindCandidateProcessIds(targetPath))
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
                // Some processes cannot be inspected due to permissions.
            }
            finally
            {
                if (!keepProcess)
                    process?.Dispose();
            }
        }

        return matches;
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

    /// <summary>
    /// Raises a user-visible supervision error without throwing from the shared timer tick.
    /// </summary>
    /// <param name="message">The error message to forward to the tray application.</param>
    private void ReportError(string message)
    {
        _errorActive = true;
        ErrorOccurred?.Invoke(this, message);
    }

    /// <summary>Clears one active lifecycle error after the helper returns to a valid state.</summary>
    private void ClearError()
    {
        if (!_errorActive)
            return;

        _errorActive = false;
        ErrorCleared?.Invoke(this);
    }

    /// <summary>
    /// Stores the runtime state of an in-progress all-instance close operation.
    /// </summary>
    private sealed class CloseOperation
    {
        /// <summary>Gets or sets whether one replacement should start after every matching process exits.</summary>
        public bool RestartAfterClose { get; set; }

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
    }
}
