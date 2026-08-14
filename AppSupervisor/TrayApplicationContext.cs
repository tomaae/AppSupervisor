using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;
using AppSupervisor.Core;
using AppSupervisor.Notifications;

namespace AppSupervisor;

/// <summary>
/// Owns the tray UI, shared supervision timer, notification routing, configuration reloads, and shutdown.
/// </summary>
public partial class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly Icon _appIcon;
    private readonly Icon _pausedIcon;
    private readonly Icon _supervisingIcon;
    private readonly Icon _errorIcon;
    private readonly Icon _startingSupervisingIcon;
    private readonly Icon _startingErrorIcon;
    private readonly Icon _stoppingIcon;
    private readonly Icon _stoppingSupervisingIcon;
    private readonly Icon _stoppingErrorIcon;
    private readonly Form _dialogOwner;
    private readonly string _configPath;
    private readonly System.Threading.Timer _monitorTimer;
    private readonly System.Threading.Timer _startupTimer;
    private readonly System.Threading.Timer _ensureClosedTimer;
    private readonly ToolStripMenuItem _pauseResumeItem;
    private readonly NotificationService _notificationService;
    private readonly SemaphoreSlim _supervisionGate = new(1, 1);
    private readonly CancellationTokenSource _supervisionCancellation = new();
    private int _monitorWorkPending;
    private int _startupWorkPending;
    private int _cleanupWorkPending;

    private AppSupervisorConfig _configuration = new();
    private List<SupervisorProfile> _profiles = [];
    private ApplicationUsageRegistry _applicationUsageRegistry = new();
    private readonly object _runtimeStateLock = new();
    private readonly object _trayStateLock = new();
    private readonly HashSet<SupervisorProfile> _reportedProfileTickErrors = [];
    private readonly HashSet<SupervisorProfile> _reportedStartupTickErrors = [];
    private volatile bool _paused = true;
    private volatile bool _pausedManually;
    private volatile bool _configurationError;
    private readonly HashSet<RuntimeErrorIdentity> _activeRuntimeErrors = [];
    private bool _inactiveCleanupError;
    private readonly record struct RuntimeErrorIdentity(SupervisorProfile Profile, IManagedResource Resource);
    private TrayStateSnapshot? _lastScheduledTrayState;
    private volatile bool _hasValidConfiguration;
    private bool _configurationEditorOpen;
    private int _configurationLoadGeneration;
    private volatile bool _exiting;

    /// <summary>
    /// Creates the tray UI, notification providers, safe configuration loader, supervision timers, and startup check.
    /// </summary>
    public TrayApplicationContext()
    {
        _configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        _appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
            ?? (Icon)SystemIcons.Application.Clone();
        _pausedIcon = TrayIconFactory.CreatePausedIcon(_appIcon);
        _supervisingIcon = TrayIconFactory.CreateSupervisingIcon(_appIcon);
        _errorIcon = TrayIconFactory.CreateErrorIcon(_appIcon);
        _startingSupervisingIcon = TrayIconFactory.CreateStartingIcon(_supervisingIcon);
        _startingErrorIcon = TrayIconFactory.CreateStartingIcon(_errorIcon);
        _stoppingIcon = TrayIconFactory.CreateStoppingIcon(_appIcon);
        _stoppingSupervisingIcon = TrayIconFactory.CreateStoppingIcon(_supervisingIcon);
        _stoppingErrorIcon = TrayIconFactory.CreateStoppingIcon(_errorIcon);

        var contextMenu = new ContextMenuStrip();

        _pauseResumeItem = new ToolStripMenuItem("Pause");
        _pauseResumeItem.Click += TogglePause;

        contextMenu.Items.Add("Configure...", null, OpenConfigurationEditor);
        contextMenu.Items.Add(_pauseResumeItem);
        contextMenu.Items.Add(_steamVrAlertsItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, Exit);

        _trayIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "AppSupervisor",
            ContextMenuStrip = contextMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += OpenConfigurationEditor;
        _dialogOwner = CreateDialogOwner();

        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The AppSupervisor executable path could not be determined.");

        _notificationService = new NotificationService(
        [
            new PopupNotificationProvider(),
            new WindowsNotificationProvider(
                executablePath,
                Path.Combine(AppContext.BaseDirectory, "AppSupervisor.NotificationHost.exe")
            ),
            new XsOverlayNotificationProvider()
        ]);

        InitializeSteamVrIntegration();

        Application.ApplicationExit += ApplicationExiting;
        _monitorTimer = new System.Threading.Timer(
            MonitorTimerTick,
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1)
        );
        _startupTimer = new System.Threading.Timer(
            StartupTimerTick,
            null,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(100)
        );
        _ensureClosedTimer = new System.Threading.Timer(
            EnsureClosedTimerTick,
            null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(5)
        );
        Application.Idle += ApplicationBecameIdle;
    }

    /// <summary>
    /// Coalesces the background timer signal into one serialized supervision cycle.
    /// </summary>
    /// <param name="state">Unused timer state.</param>
    private void MonitorTimerTick(object? state)
    {
        if (_exiting)
            return;

        if (Interlocked.Exchange(ref _monitorWorkPending, 1) != 0)
            return;

        QueueSupervisionWork(
            RunMonitorCycle,
            () => Volatile.Write(ref _monitorWorkPending, 0)
        );
    }

    /// <summary>Runs one ordered profile, cleanup-progress, and SteamVR update away from WinForms.</summary>
    private void RunMonitorCycle()
    {
        if (_paused)
            return;
        _steamVrMonitor.Advance(DateTime.UtcNow);


        foreach (var profile in _profiles)
        {
            try
            {
                bool stateChanged = profile.Update();
                lock (_runtimeStateLock)
                    _reportedProfileTickErrors.Remove(profile);

                if (!stateChanged)
                    continue;

                SupervisorLog.WriteInformation(
                    $"TRACE Profile '{profile.Name}': Update returned a trigger transition; " +
                    $"active={profile.TriggerActive}."
                );

                PublishNotification(
                    NotificationSeverity.Information,
                    "AppSupervisor",
                    $"{profile.Name}: {profile.TriggerDisplayName} is now " +
                    (profile.TriggerActive ? "running." : "stopped."),
                    profile.NotificationTargets
                );
                SupervisorLog.WriteInformation(
                    $"TRACE Profile '{profile.Name}': trigger-transition notification submitted."
                );
            }
            catch (Exception ex)
            {
                bool firstFailure;
                lock (_runtimeStateLock)
                    firstFailure = _reportedProfileTickErrors.Add(profile);
                UpdateTrayState();

                if (!firstFailure)
                    continue;

                PublishNotification(
                    NotificationSeverity.Error,
                    "Supervision error",
                    $"{profile.Name}\nUnexpected profile update failure: {ex.Message}",
                    profile.NotificationTargets
                );
            }
        }

        _applicationUsageRegistry.AdvanceCleanup();

        UpdateTrayState();
    }

    /// <summary>Coalesces a five-minute cleanup request into the serialized background worker.</summary>
    /// <param name="state">Unused timer state.</param>
    private void EnsureClosedTimerTick(object? state)
    {
        if (_exiting)
            return;

        if (Interlocked.Exchange(ref _cleanupWorkPending, 1) != 0)
            return;

        QueueSupervisionWork(
            RunEnsureClosedSweep,
            () => Volatile.Write(ref _cleanupWorkPending, 0)
        );
    }

    /// <summary>Runs one opted-in inactive-helper cleanup sweep away from WinForms.</summary>
    private void RunEnsureClosedSweep()
    {
        if (_paused)
            return;

        _applicationUsageRegistry.Sweep();
    }

    /// <summary>Restarts the inactive-helper sweep interval without tying it to the UI message pump.</summary>
    private void ResetEnsureClosedTimer()
    {
        if (_exiting)
            return;

        _ensureClosedTimer.Change(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>Coalesces the lightweight background startup timer into the serialized worker.</summary>
    /// <param name="state">Unused timer state.</param>
    private void StartupTimerTick(object? state)
    {
        if (_exiting)
            return;

        if (Interlocked.Exchange(ref _startupWorkPending, 1) != 0)
            return;

        QueueSupervisionWork(
            RunStartupCycle,
            () => Volatile.Write(ref _startupWorkPending, 0)
        );
    }

    /// <summary>Advances every dependency and delay-gated startup sequence away from WinForms.</summary>
    private void RunStartupCycle()
    {
        if (_paused)
            return;

        DateTime nowUtc = DateTime.UtcNow;
        bool startupWasPending = _profiles.Any(profile => profile.StartupPending);

        foreach (SupervisorProfile profile in _profiles)
        {
            try
            {
                profile.AdvanceStartup(nowUtc);
                lock (_runtimeStateLock)
                    _reportedStartupTickErrors.Remove(profile);
            }
            catch (Exception ex)
            {
                bool firstFailure;
                lock (_runtimeStateLock)
                    firstFailure = _reportedStartupTickErrors.Add(profile);
                UpdateTrayState();

                if (!firstFailure)
                    continue;

                PublishNotification(
                    NotificationSeverity.Error,
                    "Supervision error",
                    $"{profile.Name}\nUnexpected startup sequencing failure: {ex.Message}",
                    profile.NotificationTargets
                );
            }
        }

        if (startupWasPending != _profiles.Any(profile => profile.StartupPending))
            UpdateTrayState();
    }

    /// <summary>Queues one coalesced runtime operation without ever executing it on the caller's thread.</summary>
    /// <param name="operation">The serialized runtime mutation or poll.</param>
    /// <param name="completed">Clears the matching coalescing flag.</param>
    private void QueueSupervisionWork(Action operation, Action completed)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteSupervisionAsync(operation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_supervisionCancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                SupervisorLog.WriteError("Unexpected background supervision failure.", ex);
            }
            finally
            {
                completed();
            }
        });
    }

    /// <summary>Runs one operation under the shared supervision gate on a thread-pool thread.</summary>
    /// <param name="operation">The operation requiring exclusive access to runtime state.</param>
    private Task ExecuteSupervisionAsync(Action operation)
    {
        return Task.Run(async () =>
        {
            await _supervisionGate.WaitAsync(_supervisionCancellation.Token).ConfigureAwait(false);

            try
            {
                operation();
            }
            finally
            {
                _supervisionGate.Release();
            }
        }, _supervisionCancellation.Token);
    }

    /// <summary>
    /// Toggles supervision without starting, closing, or otherwise altering managed resources.
    /// </summary>
    /// <param name="sender">The Pause or Resume menu item.</param>
    /// <param name="e">The menu-click event data.</param>
    private async void TogglePause(object? sender, EventArgs e)
    {
        if (_exiting || (_configurationError && !_hasValidConfiguration))
            return;

        try
        {
            await ExecuteSupervisionAsync(() =>
            {
                if (_exiting)
                    return;

                _paused = !_paused;
                _pausedManually = _paused;

                if (_paused)
                {
                    _applicationUsageRegistry.SuspendCleanup();
                    _steamVrMonitor.Suspend();

                    foreach (SupervisorProfile profile in _profiles)
                        profile.SuspendMonitoring();
                }
            });
        }
        catch (OperationCanceledException) when (_exiting)
        {
            return;
        }

        if (!_paused)
        {
            ResetEnsureClosedTimer();
        }
        UpdateTrayState();
    }

    /// <summary>
    /// Builds a complete replacement configuration and swaps it into use only when loading and validation succeed.
    /// </summary>
    /// <param name="showNotification">Whether to notify after a successful manual reload.</param>
    private async Task LoadConfigurationAsync(bool showNotification)
    {
        var newProfiles = new List<SupervisorProfile>();
        var newApplicationUsageRegistry = new ApplicationUsageRegistry();
        AppSupervisorConfig newConfig;
        int loadGeneration = ++_configurationLoadGeneration;

        try
        {
            newConfig = await ConfigLoader.LoadAsync(_configPath);

            if (_exiting || loadGeneration != _configurationLoadGeneration)
            {
                newApplicationUsageRegistry.Dispose();
                return;
            }

            await Task.Run(() =>
            {
                foreach (var profileConfig in newConfig.Profiles.Where(profile => profile.Enabled))
                {
                    SupervisorProfile? profile = null;
                    profile = SupervisorProfileFactory.Create(
                        profileConfig,
                        applicationConfig => () =>
                            newApplicationUsageRegistry.IsRequiredByAnotherProfile(
                                applicationConfig.Path,
                                profile ?? throw new InvalidOperationException(
                                    "The profile close guard was evaluated before profile construction completed."
                                )
                            ),
                        newConfig.Integrations.HomeAssistant
                    );
                    profile.ResourceRestarted += OnResourceRestarted;
                    profile.ErrorOccurred += OnSupervisionError;
                    profile.ResourceNotificationRequested += OnResourceNotificationRequested;
                    newProfiles.Add(profile);
                    newApplicationUsageRegistry.RegisterProfile(profileConfig, profile);
                    profile.ErrorCleared += OnSupervisionErrorCleared;
                }

                newApplicationUsageRegistry.CompleteRegistration();
                newApplicationUsageRegistry.CleanupFailed += OnInactiveApplicationCleanupFailed;
                newApplicationUsageRegistry.CleanupRecovered += OnInactiveApplicationCleanupRecovered;
            });
        }
        catch (Exception ex)
        {
            if (_exiting || loadGeneration != _configurationLoadGeneration)
            {
                DisposeReplacementRuntime(newProfiles, newApplicationUsageRegistry);
                return;
            }

            DisposeReplacementRuntime(newProfiles, newApplicationUsageRegistry);
            try
            {
                await ExecuteSupervisionAsync(() => HandleConfigurationLoadFailure(ex));
            }
            catch (OperationCanceledException) when (_exiting)
            {
            }
            return;
        }

        if (_exiting || loadGeneration != _configurationLoadGeneration)
        {
            DisposeReplacementRuntime(newProfiles, newApplicationUsageRegistry);
            return;
        }

        bool applied = false;
        try
        {
            await ExecuteSupervisionAsync(() =>
            {
                if (_exiting || loadGeneration != _configurationLoadGeneration)
                    return;

                List<SupervisorProfile> oldProfiles = _profiles;
                ApplicationUsageRegistry oldApplicationUsageRegistry = _applicationUsageRegistry;

                _configuration = newConfig;
                _profiles = newProfiles;
                _applicationUsageRegistry = newApplicationUsageRegistry;
                _hasValidConfiguration = true;
                _configurationError = false;
                if (_paused && !_pausedManually)
                    _paused = false;

                lock (_runtimeStateLock)
                {
                    _activeRuntimeErrors.Clear();
                    _inactiveCleanupError = false;
                    _activeHealthErrors.Clear();
                    _reportedProfileTickErrors.Clear();
                    _reportedStartupTickErrors.Clear();
                }
                _steamVrMonitoringEnabled = newConfig.Integrations.SteamVr.Enabled;
                _steamVrMonitor.ApplyConfiguration(newConfig.Integrations.SteamVr);

                foreach (SupervisorProfile oldProfile in oldProfiles)
                    oldProfile.Dispose();

                oldApplicationUsageRegistry.CleanupFailed -= OnInactiveApplicationCleanupFailed;
                oldApplicationUsageRegistry.CleanupRecovered -= OnInactiveApplicationCleanupRecovered;
                oldApplicationUsageRegistry.Dispose();

                foreach (SupervisorProfile newProfile in _profiles)
                    newProfile.InitializeResources();

                applied = true;
                UpdateTrayState();
            });
        }
        catch (OperationCanceledException) when (_exiting)
        {
            DisposeReplacementRuntime(newProfiles, newApplicationUsageRegistry);
            return;
        }

        if (!applied)
        {
            DisposeReplacementRuntime(newProfiles, newApplicationUsageRegistry);
            return;
        }

        ResetEnsureClosedTimer();

        if (showNotification)
        {
            PublishNotification(
                NotificationSeverity.Information,
                "AppSupervisor",
                $"Configuration reloaded. {_profiles.Count} profile(s) active.",
                GetOperationalNotificationTargets()
            );
        }
    }

    /// <summary>Disposes a configuration graph that was built successfully but never made active.</summary>
    /// <param name="profiles">The detached profiles and their resource subscriptions.</param>
    /// <param name="applicationUsageRegistry">The detached cross-profile helper registry.</param>
    private static void DisposeReplacementRuntime(
        IEnumerable<SupervisorProfile> profiles,
        ApplicationUsageRegistry applicationUsageRegistry)
    {
        foreach (SupervisorProfile profile in profiles)
            profile.Dispose();

        applicationUsageRegistry.Dispose();
    }

    /// <summary>
    /// Preserves the last accepted configuration and reports a failed startup load or replacement load.
    /// </summary>
    /// <param name="exception">The validation or profile-construction failure.</param>
    private void HandleConfigurationLoadFailure(Exception exception)
    {
        _configurationError = true;

        if (!_hasValidConfiguration)
        {
            _paused = true;
            _pausedManually = false;
        }

        UpdateTrayState();

        string prefix = _hasValidConfiguration
            ? "Reload failed. Existing configuration remains active."
            : "Configuration is invalid. Supervision is paused.";

        PublishNotification(
            NotificationSeverity.Error,
            "Configuration error",
            $"{prefix}\n{exception.Message}",
            GetOperationalNotificationTargets()
        );
    }

    /// <summary>
    /// Sends a warning after Windows accepts a supervised application or service restart request.
    /// </summary>
    /// <param name="profile">The supervisor profile that owns the restarted resource.</param>
    /// <param name="resource">The managed resource that was restarted.</param>
    private void OnResourceRestarted(
        SupervisorProfile profile,
        IManagedResource resource)
    {
        PublishNotification(
            NotificationSeverity.Warning,
            "Resource restarted",
            $"{profile.Name}: {resource.DisplayName} was restarted.",
            resource.NotificationTargets
        );
    }

    /// <summary>Reports a failed inactive-helper cleanup through its merged application notification targets.</summary>
    /// <param name="displayName">The helper executable filename.</param>
    /// <param name="message">The close failure reported after all configured attempts.</param>
    /// <param name="targets">The combined notification targets from opted-in entries.</param>
    private void OnInactiveApplicationCleanupFailed(
        string displayName,
        string message,
        IReadOnlyList<NotificationTarget> targets)
    {
        lock (_runtimeStateLock)
            _inactiveCleanupError = true;
        UpdateTrayState();

        PublishNotification(
            NotificationSeverity.Error,
            "Inactive helper close failed",
            $"{displayName}\n{message}",
            targets
        );
    }

    /// <summary>Clears inactive-helper cleanup error state after a later sweep succeeds.</summary>
    private void OnInactiveApplicationCleanupRecovered()
    {
        lock (_runtimeStateLock)
            _inactiveCleanupError = false;
        UpdateTrayState();
    }

    /// <summary>
    /// Records persistent error state and sends an error when a recovery or supervision operation fails.
    /// </summary>
    /// <param name="profile">The supervisor profile that owns the failing resource.</param>
    /// <param name="resource">The managed resource that reported the error.</param>
    /// <param name="message">The user-readable supervision failure.</param>
    private void OnSupervisionError(
        SupervisorProfile profile,
        IManagedResource resource,
        string message)
    {
        lock (_runtimeStateLock)
            _activeRuntimeErrors.Add(new RuntimeErrorIdentity(profile, resource));
        UpdateTrayState();

        PublishNotification(
            NotificationSeverity.Error,
            "Supervision error",
            $"{profile.Name} - {resource.DisplayName}\n{message}",
            resource.NotificationTargets
        );
    }

    /// <summary>Clears a resource's ordinary tray error state after successful lifecycle supervision resumes.</summary>
    /// <param name="profile">The supervisor profile that owns the recovered resource.</param>
    /// <param name="resource">The managed resource that recovered.</param>
    private void OnSupervisionErrorCleared(
        SupervisorProfile profile,
        IManagedResource resource)
    {
        lock (_runtimeStateLock)
            _activeRuntimeErrors.Remove(new RuntimeErrorIdentity(profile, resource));
        UpdateTrayState();
    }

    /// <summary>
    /// Checks the current user's Windows startup registration and asks before enabling it when absent.
    /// </summary>
    private void CheckStartupRegistration()
    {
        try
        {
            if (StartupRegistration.IsEnabled())
                return;

            DialogResult result = MessageBox.Show(
                _dialogOwner,
                "Start AppSupervisor automatically when you sign in to Windows?",
                "AppSupervisor",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (_exiting || result != DialogResult.Yes)
                return;

            StartupRegistration.Enable();

            PublishNotification(
                NotificationSeverity.Information,
                "AppSupervisor",
                "AppSupervisor will start automatically when you sign in to Windows.",
                GetOperationalNotificationTargets()
            );
        }
        catch (Exception ex)
        {
            PublishNotification(
                NotificationSeverity.Error,
                "Windows startup error",
                $"Startup registration could not be checked or updated.\n{ex.Message}",
                GetOperationalNotificationTargets()
            );
        }
    }

    /// <summary>Runs the one-time startup-registration check only after the main application message loop has started.</summary>
    /// <param name="sender">The WinForms application lifecycle.</param>
    /// <param name="e">The idle event data.</param>
    private async void ApplicationBecameIdle(object? sender, EventArgs e)
    {
        Application.Idle -= ApplicationBecameIdle;

        if (_exiting)
            return;

        await LoadConfigurationAsync(showNotification: false);

        if (!_exiting)
            CheckStartupRegistration();
    }

    /// <summary>
    /// Publishes provider-independent content to the notification router.
    /// </summary>
    /// <param name="severity">The presentation severity.</param>
    /// <param name="title">The notification heading.</param>
    /// <param name="message">The detailed notification text.</param>
    /// <param name="targets">The configured presentation targets.</param>
    private void PublishNotification(
        NotificationSeverity severity,
        string title,
        string message,
        IEnumerable<NotificationTarget> targets)
    {
        _notificationService.Publish(new SupervisorNotification(
            severity,
            title,
            message,
            targets
        ));
    }

    /// <summary>
    /// Combines active profile targets for application-level messages and provides a popup before any valid config exists.
    /// </summary>
    /// <returns>The distinct targets used for configuration and startup messages.</returns>
    private IReadOnlyList<NotificationTarget> GetOperationalNotificationTargets()
    {
        NotificationTarget[] targets = _profiles
            .SelectMany(profile => profile.NotificationTargets)
            .Distinct()
            .ToArray();

        if (_hasValidConfiguration || targets.Length > 0)
            return targets;

        return [NotificationTarget.Popup];
    }

    /// <summary>
    /// Applies the highest-priority tray badge and text for pause, errors, configuration availability, or active supervision.
    /// </summary>
    private void UpdateTrayState()
    {
        bool hasRuntimeError;

        lock (_runtimeStateLock)
        {
            hasRuntimeError =
                _inactiveCleanupError ||
                _activeRuntimeErrors.Count > 0 ||
                _activeHealthErrors.Count > 0 ||
                _reportedProfileTickErrors.Count > 0 ||
                _reportedStartupTickErrors.Count > 0;
        }

        bool pauseEnabled = !(_configurationError && !_hasValidConfiguration);
        bool startupPending = !_paused && _profiles.Any(profile => profile.StartupPending);
        bool waitingForCloseTimeout = !_paused &&
            _profiles.Any(profile => profile.WaitingForCloseTimeout);
        bool resourceDeactivationPending = !_paused &&
            _profiles.Any(profile => profile.ResourceDeactivationPending);
        bool shutdownPending = waitingForCloseTimeout || resourceDeactivationPending;
        bool supervisionActive = _profiles.Any(profile => profile.TriggerActive);
        string shutdownText = resourceDeactivationPending
            ? "closing helpers"
            : "waiting to close helpers";
        string pauseText = _paused
            ? "Resume"
            : "Pause";
        Icon icon;
        string text;

        if (_pausedManually)
        {
            icon = _pausedIcon;
            text = "AppSupervisor - Paused";
        }
        else if (_configurationError)
        {
            icon = _errorIcon;
            text = "AppSupervisor - Configuration error";
        }
        else if (hasRuntimeError || _hasSteamVrOfflineDevices)
        {
            icon = shutdownPending
                ? _stoppingErrorIcon
                : startupPending
                    ? _startingErrorIcon
                    : _errorIcon;
            text = shutdownPending
                ? $"AppSupervisor - Supervision error; {shutdownText}"
                : startupPending
                    ? "AppSupervisor - Supervision error; starting helpers"
                    : "AppSupervisor - Supervision error";
        }
        else if (_profiles.Count == 0 && !_steamVrMonitoringEnabled)
        {
            icon = _errorIcon;
            text = "AppSupervisor - No enabled profiles";
        }
        else if (_profiles.Count == 0)
        {
            icon = _appIcon;
            text = "AppSupervisor - Monitoring SteamVR";
        }
        else if (_paused)
        {
            icon = _pausedIcon;
            text = "AppSupervisor - Paused";
        }
        else if (shutdownPending)
        {
            icon = supervisionActive ? _stoppingSupervisingIcon : _stoppingIcon;
            text = supervisionActive
                ? $"AppSupervisor - Supervising; {shutdownText}"
                : $"AppSupervisor - {char.ToUpperInvariant(shutdownText[0])}{shutdownText[1..]}";
        }
        else if (supervisionActive)
        {
            icon = startupPending ? _startingSupervisingIcon : _supervisingIcon;
            text = startupPending
                ? "AppSupervisor - Starting helpers"
                : "AppSupervisor - Supervising";
        }
        else
        {
            icon = _appIcon;
            text = "AppSupervisor - Waiting for monitored applications";
        }

        var state = new TrayStateSnapshot(pauseEnabled, pauseText, icon, text);

        lock (_trayStateLock)
        {
            if (_lastScheduledTrayState is TrayStateSnapshot previous &&
                previous.Matches(state))
            {
                return;
            }

            _lastScheduledTrayState = state;
            RunOnUiThread(() => ApplyTrayState(state));
        }
    }

    /// <summary>Applies a precomputed immutable tray snapshot on the WinForms thread.</summary>
    private void ApplyTrayState(TrayStateSnapshot state)
    {
        if (_exiting)
            return;

        bool transition = !string.Equals(_trayIcon.Text, state.Text, StringComparison.Ordinal);

        if (transition)
        {
            SupervisorLog.WriteInformation(
                $"TRACE Tray transition applying: '{_trayIcon.Text}' -> '{state.Text}'."
            );
        }

        _pauseResumeItem.Enabled = state.PauseEnabled;
        _pauseResumeItem.Text = state.PauseText;
        _trayIcon.Icon = state.Icon;
        _trayIcon.Text = state.Text;

        if (transition)
            SupervisorLog.WriteInformation($"TRACE Tray transition applied: '{state.Text}'.");
    }

    /// <summary>Describes every user-visible tray value that must change as one UI update.</summary>
    private readonly record struct TrayStateSnapshot(
        bool PauseEnabled,
        string PauseText,
        Icon Icon,
        string Text)
    {
        /// <summary>Compares stable tray values without relying on native icon-handle equality.</summary>
        public bool Matches(TrayStateSnapshot other)
        {
            return PauseEnabled == other.PauseEnabled &&
                string.Equals(PauseText, other.PauseText, StringComparison.Ordinal) &&
                ReferenceEquals(Icon, other.Icon) &&
                string.Equals(Text, other.Text, StringComparison.Ordinal);
        }
    }

    /// <summary>Posts a UI-only operation without waiting for the WinForms message pump.</summary>
    /// <param name="operation">The tray or window operation to post.</param>
    private void RunOnUiThread(Action operation)
    {
        if (_exiting || _dialogOwner.IsDisposed)
            return;

        try
        {
            if (_dialogOwner.InvokeRequired)
                _dialogOwner.BeginInvoke(operation);
            else
                operation();
        }
        catch (InvalidOperationException)
        {
            // Shutdown destroyed the hidden owner before this background update was posted.
        }
    }

    /// <summary>
    /// Stops AppSupervisor, cancels notifications and runtime work, releases tray resources, and leaves managed resources untouched.
    /// </summary>
    /// <param name="sender">The Exit menu item.</param>
    /// <param name="e">The menu-click event data.</param>
    private async void Exit(object? sender, EventArgs e)
    {
        if (_exiting)
            return;

        _exiting = true;
        Application.Idle -= ApplicationBecameIdle;
        _configurationLoadGeneration++;
        CloseAllWindows();
        SaveVerifiedConfigurationBackup();
        Application.ApplicationExit -= ApplicationExiting;
        _paused = true;
        _monitorTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _startupTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _ensureClosedTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        await ExecuteSupervisionAsync(() =>
        {
            _applicationUsageRegistry.CleanupRecovered -= OnInactiveApplicationCleanupRecovered;
            _applicationUsageRegistry.CleanupFailed -= OnInactiveApplicationCleanupFailed;
            _applicationUsageRegistry.Dispose();

            foreach (SupervisorProfile profile in _profiles)
                profile.Dispose();

            _profiles.Clear();
            DisposeSteamVrIntegration();
        });

        _supervisionCancellation.Cancel();
        await Task.Run(_notificationService.Dispose);

        _ensureClosedTimer.Dispose();
        _startupTimer.Dispose();
        _monitorTimer.Dispose();

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _stoppingErrorIcon.Dispose();
        _stoppingSupervisingIcon.Dispose();
        _stoppingIcon.Dispose();
        _errorIcon.Dispose();
        _startingErrorIcon.Dispose();
        _pausedIcon.Dispose();
        _appIcon.Dispose();
        _startingSupervisingIcon.Dispose();
        _supervisingIcon.Dispose();

        ExitThread();
    }

    /// <summary>Creates an invisible owner whose destruction also dismisses tray-level native modal prompts.</summary>
    /// <returns>The hidden form used as the owner of startup and lifecycle message boxes.</returns>
    private static Form CreateDialogOwner()
    {
        var owner = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(1, 1),
            Opacity = 0
        };

        _ = owner.Handle;
        return owner;
    }

    /// <summary>Closes every AppSupervisor form and destroys the owner of any tray-level native modal.</summary>
    private void CloseAllWindows()
    {
        foreach (Form form in Application.OpenForms.Cast<Form>().ToArray())
        {
            if (!form.IsDisposed && !form.InvokeRequired)
            {
                if (form is ConfigurationEditorForm configurationEditor)
                    configurationEditor.CloseWithoutUnsavedChangesPrompt();
                else
                    form.Close();
            }
        }

        if (!_dialogOwner.IsDisposed)
            _dialogOwner.Dispose();
    }
}
