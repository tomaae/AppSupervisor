using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;
using AppSupervisor.Core;
using AppSupervisor.Notifications;
using AppSupervisor.SteamVr;
using AppSupervisor.StreamDeck;
using AppSupervisor.SupervisorApi;
using AppSupervisor.Twitch;
using Microsoft.Win32;

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
    private readonly System.Threading.Timer _lifecycleTimer;
    private readonly System.Threading.Timer _ensureClosedTimer;
    private readonly System.Threading.Timer _twitchAuthorizationTimer;
    private readonly ToolStripMenuItem _pauseResumeItem;
    private readonly NotificationService _notificationService;
    private readonly SupervisorApiServer _supervisorApi = new();
    private readonly StreamDeckStatusImages _streamDeckStatusImages;
    private readonly StreamDeckStatusClient _streamDeckStatusClient;
    private readonly SemaphoreSlim _supervisionGate = new(1, 1);
    private readonly CancellationTokenSource _supervisionCancellation = new();
    private int _monitorWorkPending;
    private int _lifecycleWorkPending;
    private int _cleanupWorkPending;
    private int _twitchValidationPending;
    private int _twitchReauthorizationPromptShown;
    private int _pauseVisualPending;
    private bool _monitorPreferSharedSnapshot;
    private bool _lifecyclePreferSharedSnapshot;

    private AppSupervisorConfig _configuration = new();
    private List<SupervisorProfile> _profiles = [];
    private ApplicationUsageRegistry _applicationUsageRegistry = new();
    private readonly object _runtimeStateLock = new();
    private readonly object _trayStateLock = new();
    private readonly Dictionary<SupervisorProfile, ActiveTrayError> _reportedProfileTickErrors = [];
    private readonly Dictionary<SupervisorProfile, ActiveTrayError> _reportedLifecycleTickErrors = [];
    private volatile bool _paused = true;
    private volatile bool _pausing;
    private bool _pauseDrainStarted;
    private volatile bool _pausedManually;
    private volatile bool _configurationLoadError;
    private string _configurationLoadErrorTrayStatus = "Configuration error";
    private readonly Dictionary<RuntimeErrorIdentity, ActiveTrayError> _activeRuntimeErrors = [];
    private readonly ActiveNotificationDeduplicator<RuntimeErrorNotificationIdentity>
        _runtimeErrorNotifications = new();
    private ActiveTrayError? _inactiveCleanupError;
    private long _trayErrorSequence;
    private readonly record struct RuntimeErrorIdentity(SupervisorProfile Profile, IManagedResource Resource);
    private readonly record struct RuntimeErrorNotificationIdentity(
        SupervisorProfile Profile,
        IManagedResource Resource,
        string Message);
    private readonly record struct ActiveTrayError(long Sequence, string Summary);
    private readonly record struct ActiveTrayErrorSnapshot(int Count, string? LatestSummary);
    private TrayStateSnapshot? _lastScheduledTrayState;
    private volatile bool _hasValidConfiguration;
    private volatile bool _configurationEditorOpen;
    private ConfigurationRuntimeStatusSnapshot _configurationRuntimeStatusSnapshot =
        ConfigurationRuntimeStatusSnapshot.Empty;
    private int _configurationLoadGeneration;
    private volatile bool _exiting;
    private volatile bool _systemSuspended;
    private volatile bool _resumeResetPending;

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

        _streamDeckStatusImages = StreamDeckStatusImages.Create(
            Application.ExecutablePath,
            _appIcon
        );
        _streamDeckStatusClient = new StreamDeckStatusClient(new StreamDeckStatusSnapshot(
            StreamDeckVisualState.Idle,
            "Starting",
            "AppSupervisor - Starting",
            _streamDeckStatusImages[StreamDeckVisualState.Idle]
        ));
        _streamDeckStatusClient.ConfigurationRequested += StreamDeckConfigurationRequested;

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
        _lifecycleTimer = new System.Threading.Timer(
            LifecycleTimerTick,
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
        _ensureClosedTimer = new System.Threading.Timer(
            EnsureClosedTimerTick,
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
        _twitchAuthorizationTimer = new System.Threading.Timer(
            TwitchAuthorizationTimerTick,
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
        SystemEvents.PowerModeChanged += SystemPowerModeChanged;
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
        if (_paused || _pausing || _systemSuspended || _resumeResetPending)
            return;
        ProcessPathSnapshot.BeginCycle(_monitorPreferSharedSnapshot);
        _steamVrMonitor.Advance(DateTime.UtcNow);


        foreach (var profile in _profiles)
        {
            try
            {
                bool stateChanged = profile.Update(
                    observeInactiveRuntimeStatus: _configurationEditorOpen
                );
                lock (_runtimeStateLock)
                    _reportedProfileTickErrors.Remove(profile);

                if (!stateChanged)
                    continue;

                SupervisorLog.WriteTrace(
                    $"Profile '{profile.Name}': Update returned a trigger transition; " +
                    $"active={profile.TriggerActive}."
                );
            }
            catch (Exception ex)
            {
                bool firstFailure;
                lock (_runtimeStateLock)
                {
                    firstFailure = !_reportedProfileTickErrors.ContainsKey(profile);
                    _reportedProfileTickErrors[profile] = CreateActiveTrayError(
                        TrayTooltipText.CreateErrorSummary(
                            $"{profile.Name} profile update failed",
                            ex.Message
                        )
                    );
                }
                UpdateTrayState();

                if (!firstFailure)
                    continue;

                PublishSystemNotification(
                    NotificationSeverity.Error,
                    "Profile update failed",
                    $"{profile.Name}\nUnexpected profile update failure: {ex.Message}"
                );
            }
        }

        _monitorPreferSharedSnapshot = ProcessPathSnapshot.ShouldPreferSharedSnapshotNextCycle;
        PublishSupervisorApiSnapshot();
        PublishConfigurationRuntimeStatusSnapshot();
        ResetLifecycleTimer();
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
        if (_paused || _pausing || _systemSuspended || _resumeResetPending)
            return;

        ProcessPathSnapshot.BeginCycle(preferSharedSnapshot: false);
        _applicationUsageRegistry.Sweep();
        PublishConfigurationRuntimeStatusSnapshot();
        ResetLifecycleTimer();
    }

    /// <summary>Enables the inactive-helper sweep only while effective cleanup targets can be supervised.</summary>
    private void ResetEnsureClosedTimer()
    {
        if (_exiting || _paused || _pausing || _systemSuspended || _resumeResetPending ||
            !_applicationUsageRegistry.HasCleanupTargets)
        {
            _ensureClosedTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        _ensureClosedTimer.Change(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>Validates a stored Twitch OAuth session at startup and hourly as Twitch requires.</summary>
    private void ResetTwitchAuthorizationTimer()
    {
        if (_exiting)
        {
            _twitchAuthorizationTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        _twitchAuthorizationTimer.Change(TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        TwitchAuthorizationTimerTick(null);
    }

    private void TwitchAuthorizationTimerTick(object? state)
    {
        if (_exiting || Interlocked.Exchange(ref _twitchValidationPending, 1) != 0)
            return;
        _ = ValidateTwitchAuthorizationAsync();
    }

    private async Task ValidateTwitchAuthorizationAsync()
    {
        try
        {
            if (_exiting)
                return;
            using var authorization = new TwitchAuthorizationService(
                new TwitchIntegrationConfig()
            );
            await authorization.GetStatusAsync(_supervisionCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_exiting)
        {
        }
        catch (TwitchReauthorizationRequiredException ex)
        {
            SupervisorLog.WriteError("The stored Twitch authorization requires renewed consent.", ex);
            ShowTwitchReauthorizationPrompt(ex.Message);
        }
        catch (Exception ex)
        {
            SupervisorLog.WriteError("The stored Twitch authorization could not be validated.", ex);
        }
        finally
        {
            Volatile.Write(ref _twitchValidationPending, 0);
        }
    }

    /// <summary>Shows one actionable reconnect window per process before a Twitch action fails.</summary>
    private void ShowTwitchReauthorizationPrompt(string detail)
    {
        if (Interlocked.Exchange(ref _twitchReauthorizationPromptShown, 1) != 0)
            return;

        RunOnUiThread(() =>
        {
            if (_exiting)
                return;

            using var dialog = new TwitchReauthorizationDialog(detail);
            dialog.ShowDialog(_dialogOwner);
        });
    }

    /// <summary>Coalesces the demand-driven lifecycle timer into the serialized worker.</summary>
    /// <param name="state">Unused timer state.</param>
    private void LifecycleTimerTick(object? state)
    {
        if (_exiting)
            return;

        if (Interlocked.Exchange(ref _lifecycleWorkPending, 1) != 0)
            return;

        QueueSupervisionWork(
            RunLifecycleCycle,
            () => Volatile.Write(ref _lifecycleWorkPending, 0)
        );
    }

    /// <summary>Advances starts, closes, minimization, and startup sequencing away from WinForms.</summary>
    private void RunLifecycleCycle()
    {
        if (_paused || _systemSuspended || _resumeResetPending)
            return;

        DateTime nowUtc = SupervisorTime.UtcNow;
        bool workWasPending = HasLifecycleWork();
        ProcessPathSnapshot.BeginCycle(_lifecyclePreferSharedSnapshot);

        foreach (SupervisorProfile profile in _profiles)
        {
            try
            {
                profile.AdvanceLifecycle(nowUtc);
                lock (_runtimeStateLock)
                    _reportedLifecycleTickErrors.Remove(profile);
            }
            catch (Exception ex)
            {
                bool firstFailure;
                lock (_runtimeStateLock)
                {
                    firstFailure = !_reportedLifecycleTickErrors.ContainsKey(profile);
                    _reportedLifecycleTickErrors[profile] = CreateActiveTrayError(
                        TrayTooltipText.CreateErrorSummary(
                            $"{profile.Name} lifecycle failed",
                            ex.Message
                        )
                    );
                }
                UpdateTrayState();

                if (!firstFailure)
                    continue;

                PublishSystemNotification(
                    NotificationSeverity.Error,
                    "Profile lifecycle failed",
                    $"{profile.Name}\nUnexpected lifecycle failure: {ex.Message}"
                );
            }
        }

        if (_pausing && _pauseDrainStarted)
        {
            foreach (SupervisorProfile profile in _profiles)
                profile.AdvancePauseDrain();

            _steamVrMonitor.AdvancePauseDrain();
        }

        _applicationUsageRegistry.AdvanceLifecycle(nowUtc);
        _lifecyclePreferSharedSnapshot = ProcessPathSnapshot.ShouldPreferSharedSnapshotNextCycle;
        PublishConfigurationRuntimeStatusSnapshot();

        bool workPending = HasLifecycleWork();
        if (workWasPending != workPending)
            UpdateTrayState();

        ResetLifecycleTimer();
    }

    /// <summary>Gets whether any profile or inactive cleanup still needs the 100ms lifecycle timer.</summary>
    private bool HasLifecycleWork()
    {
        return _profiles.Any(profile => profile.LifecycleWorkPending) ||
            _applicationUsageRegistry.LifecycleWorkPending ||
            (_pausing && _pauseDrainStarted &&
                (_profiles.Any(profile => profile.PauseDrainPending) ||
                    _steamVrMonitor.PauseDrainPending));
    }

    /// <summary>Gets whether lifecycle work needs another pass without waiting for a delay deadline.</summary>
    private bool HasImmediateLifecycleWork()
    {
        return _profiles.Any(profile => profile.ImmediateLifecycleWorkPending) ||
            _applicationUsageRegistry.LifecycleWorkPending ||
            (_pausing && _pauseDrainStarted &&
                (_profiles.Any(profile => profile.PauseDrainPending) ||
                    _steamVrMonitor.PauseDrainPending));
    }

    /// <summary>Enables the lifecycle timer only while transitions or startup sequencing need it.</summary>
    private void ResetLifecycleTimer()
    {
        bool hasWork = HasLifecycleWork();

        if (_exiting || _paused || _systemSuspended || _resumeResetPending || !hasWork)
        {
            _lifecycleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            if (!_systemSuspended && _pausing && _pauseDrainStarted && !hasWork)
                CompletePause();

            return;
        }

        if (HasImmediateLifecycleWork())
        {
            _lifecycleTimer.Change(
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(100)
            );
            return;
        }

        DateTime nowUtc = SupervisorTime.UtcNow;
        DateTime nextDueUtc = _profiles
            .Select(profile => profile.NextStartupDueUtc)
            .Where(dueUtc => dueUtc is not null)
            .Select(dueUtc => dueUtc!.Value)
            .DefaultIfEmpty(nowUtc + TimeSpan.FromMilliseconds(100))
            .Min();
        TimeSpan due = nextDueUtc > nowUtc
            ? nextDueUtc - nowUtc
            : TimeSpan.FromMilliseconds(1);
        _lifecycleTimer.Change(due, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Commits manual pause only after every already-issued lifecycle task has settled.</summary>
    private void CompletePause()
    {
        if (!_pausing || !_pauseDrainStarted || HasLifecycleWork())
            return;

        _applicationUsageRegistry.SuspendCleanup();
        _steamVrMonitor.Suspend();

        foreach (SupervisorProfile profile in _profiles)
            profile.SuspendMonitoring();

        _paused = true;
        _pausing = false;
        _pauseDrainStarted = false;
        _lifecycleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Volatile.Write(ref _pauseVisualPending, 1);
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
                await ExecuteSupervisionCoreAsync(operation).ConfigureAwait(false);
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

                if (Interlocked.Exchange(ref _pauseVisualPending, 0) != 0)
                    UpdateTrayState();
            }
        });
    }

    /// <summary>Runs one operation under the shared supervision gate on a thread-pool thread.</summary>
    /// <param name="operation">The operation requiring exclusive access to runtime state.</param>
    private Task ExecuteSupervisionAsync(Action operation)
    {
        return Task.Run(
            () => ExecuteSupervisionCoreAsync(operation),
            _supervisionCancellation.Token
        );
    }

    /// <summary>Executes one operation under the common gate without scheduling a second worker.</summary>
    private async Task ExecuteSupervisionCoreAsync(Action operation)
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
    }

    /// <summary>Freezes supervision deadlines across Windows sleep and hibernation.</summary>
    private void SystemPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (_exiting)
            return;

        if (e.Mode == PowerModes.Suspend)
        {
            _systemSuspended = true;
            SupervisorTime.Suspend();
            _monitorTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _lifecycleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _ensureClosedTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        if (e.Mode != PowerModes.Resume)
            return;

        _resumeResetPending = true;
        SupervisorTime.Resume();
        _systemSuspended = false;
        QueueSupervisionWork(ResumeAfterSystemSuspend, static () => { });
    }

    /// <summary>Restarts only the timers allowed by the current pause and configuration state.</summary>
    private void ResumeAfterSystemSuspend()
    {
        if (_exiting || _systemSuspended)
            return;

        try
        {
            _applicationUsageRegistry.SuspendCleanup();
            _steamVrMonitor.ResetAfterSystemSuspend();

            foreach (SupervisorProfile profile in _profiles)
                profile.SuspendMonitoring();
        }
        catch (Exception ex)
        {
            SupervisorLog.WriteError("Could not fully reset monitoring after system resume.", ex);
        }
        finally
        {
            _resumeResetPending = false;
        }

        if (!_paused && !_pausing && !_systemSuspended)
            _monitorTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        ResetEnsureClosedTimer();
        ResetLifecycleTimer();
        ResetTwitchAuthorizationTimer();
    }

    /// <summary>
    /// Toggles supervision without starting, closing, or otherwise altering managed resources.
    /// </summary>
    /// <param name="sender">The Pause or Resume menu item.</param>
    /// <param name="e">The menu-click event data.</param>
    private async void TogglePause(object? sender, EventArgs e)
    {
        if (_exiting || _pausing || (_configurationLoadError && !_hasValidConfiguration))
            return;

        if (!_paused)
        {
            _pausing = true;
            _pausedManually = true;
            _monitorTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _ensureClosedTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            UpdateTrayState();
        }

        try
        {
            await ExecuteSupervisionAsync(() =>
            {
                if (_exiting)
                    return;

                if (_paused)
                {
                    _paused = false;
                    _pausedManually = false;

                    if (!_systemSuspended && !_resumeResetPending)
                        _monitorTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                }
                else
                {
                    foreach (SupervisorProfile profile in _profiles)
                        profile.BeginPauseDrain();

                    _pauseDrainStarted = true;
                    ResetLifecycleTimer();
                }
            });
        }
        catch (OperationCanceledException) when (_exiting)
        {
            return;
        }

        ResetEnsureClosedTimer();
        ResetLifecycleTimer();
        Volatile.Write(ref _pauseVisualPending, 0);
        PublishSupervisorApiSnapshot();
        PublishConfigurationRuntimeStatusSnapshot();
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
                        newConfig.Integrations.HomeAssistant,
                        newConfig.Integrations.Obs,
                        newConfig.Integrations.Twitch
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
                SupervisorLog.SetMinimumLevel(newConfig.Integrations.LogLevel);
                _profiles = newProfiles;
                _applicationUsageRegistry = newApplicationUsageRegistry;
                _hasValidConfiguration = true;
                _configurationLoadError = false;
                _configurationLoadErrorTrayStatus = "Configuration error";
                if (_paused && !_pausedManually)
                {
                    _paused = false;

                    if (!_systemSuspended && !_resumeResetPending)
                        _monitorTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                }

                lock (_runtimeStateLock)
                {
                    _activeRuntimeErrors.Clear();
                    _runtimeErrorNotifications.Clear();
                    _inactiveCleanupError = null;
                    _activeHealthErrors.Clear();
                    _reportedProfileTickErrors.Clear();
                    _reportedLifecycleTickErrors.Clear();
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

                PublishSupervisorApiSnapshot();
                PublishConfigurationRuntimeStatusSnapshot();
                try
                {
                    _supervisorApi.ApplyConfiguration(newConfig.Integrations.SupervisorApi);
                }
                catch (Exception exception)
                {
                    SupervisorLog.WriteError("Could not apply Supervisor API configuration.", exception);
                }

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
        ResetLifecycleTimer();

        if (showNotification)
        {
            PublishSystemNotification(
                NotificationSeverity.Information,
                "AppSupervisor",
                $"Configuration reloaded. {_profiles.Count} profile(s) active."
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

    /// <summary>Publishes one immutable API document without performing any system queries.</summary>
    private void PublishSupervisorApiSnapshot()
    {
        _supervisorApi.Publish(SupervisorApiSnapshotFactory.Create(
            _configuration,
            _profiles,
            _paused || _pausing || _systemSuspended || _resumeResetPending
        ));
    }

    /// <summary>
    /// Preserves the last accepted configuration and reports a failed startup load or replacement load.
    /// </summary>
    /// <param name="exception">The validation or profile-construction failure.</param>
    private void HandleConfigurationLoadFailure(Exception exception)
    {
        ConfigurationLoadFailurePresentation presentation =
            ConfigurationLoadFailureClassifier.Classify(exception, _hasValidConfiguration);
        _configurationLoadError = true;
        _configurationLoadErrorTrayStatus = TrayTooltipText.CreateErrorSummary(
            presentation.TrayStatus,
            exception.Message
        );
        SupervisorLog.WriteError(presentation.LogMessage, exception);

        if (!_hasValidConfiguration)
        {
            _paused = true;
            _pausedManually = false;
            _monitorTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _lifecycleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _ensureClosedTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        UpdateTrayState();

        PublishSystemNotification(
            NotificationSeverity.Error,
            presentation.NotificationTitle,
            $"{presentation.MessagePrefix}\n{exception.Message}"
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
        PublishResourceNotification(
            NotificationSeverity.Warning,
            "Resource restarted",
            $"{profile.Name}: {resource.DisplayName} was restarted.",
            resource
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
            _inactiveCleanupError = CreateActiveTrayError(
                TrayTooltipText.CreateErrorSummary(
                    $"{displayName} close failed",
                    message
                )
            );
        UpdateTrayState();

        PublishSharedApplicationNotification(
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
            _inactiveCleanupError = null;
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
        bool firstNotification;
        lock (_runtimeStateLock)
        {
            _activeRuntimeErrors[new RuntimeErrorIdentity(profile, resource)] =
                CreateActiveTrayError(
                    TrayTooltipText.CreateErrorSummary(
                        $"{profile.Name} - {resource.DisplayName}",
                        message
                    )
                );
            firstNotification = _runtimeErrorNotifications.TryActivate(
                new RuntimeErrorNotificationIdentity(profile, resource, message)
            );
        }
        UpdateTrayState();

        if (!firstNotification)
            return;

        PublishResourceNotification(
            NotificationSeverity.Error,
            $"{resource.DisplayName} supervision failed",
            $"{profile.Name} - {resource.DisplayName}\n{message}",
            resource
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
        {
            _activeRuntimeErrors.Remove(new RuntimeErrorIdentity(profile, resource));
            _runtimeErrorNotifications.ClearWhere(identity =>
                ReferenceEquals(identity.Profile, profile) &&
                ReferenceEquals(identity.Resource, resource)
            );
        }
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

            PublishSystemNotification(
                NotificationSeverity.Information,
                "AppSupervisor",
                "AppSupervisor will start automatically when you sign in to Windows."
            );
        }
        catch (Exception ex)
        {
            PublishSystemNotification(
                NotificationSeverity.Error,
                "Windows startup error",
                $"Startup registration could not be checked or updated.\n{ex.Message}"
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

        // Twitch public-client refresh tokens are rotating, one-time credentials. Keep
        // validation active before profiles can attempt a Twitch action and even when no
        // Twitch profile action happens for a long time.
        ResetTwitchAuthorizationTimer();

        await LoadConfigurationAsync(showNotification: false);

        if (!_exiting)
            CheckStartupRegistration();
    }

    /// <summary>
    /// Publishes a same-executable cleanup notification through the targets of entries that
    /// explicitly enabled cleanup for that helper.
    /// </summary>
    /// <param name="severity">The presentation severity.</param>
    /// <param name="title">The notification heading.</param>
    /// <param name="message">The detailed notification text.</param>
    /// <param name="targets">The configured presentation targets.</param>
    private void PublishSharedApplicationNotification(
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

    /// <summary>Publishes an AppSupervisor-level message without borrowing helper settings.</summary>
    private void PublishSystemNotification(
        NotificationSeverity severity,
        string title,
        string message)
    {
        _notificationService.Publish(ScopedNotificationFactory.CreateSystem(
            severity,
            title,
            message
        ));
    }

    /// <summary>Publishes a lifecycle message through only its owning helper's settings.</summary>
    private void PublishResourceNotification(
        NotificationSeverity severity,
        string title,
        string message,
        IManagedResource resource)
    {
        _notificationService.Publish(ScopedNotificationFactory.CreateResource(
            severity,
            title,
            message,
            resource
        ));
    }

    /// <summary>
    /// Applies the highest-priority tray badge and text for pause, errors, configuration availability, or active supervision.
    /// </summary>
    private void UpdateTrayState()
    {
        if (Volatile.Read(ref _pauseVisualPending) != 0)
            return;

        ActiveTrayErrorSnapshot runtimeErrors;

        lock (_runtimeStateLock)
            runtimeErrors = GetActiveTrayErrorSnapshot();

        bool hasRuntimeError = runtimeErrors.Count > 0;

        bool pauseEnabled = !_pausing && !(_configurationLoadError && !_hasValidConfiguration);
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
        string pauseText = _pausing
            ? "Pausing..."
            : _paused
                ? "Resume"
                : "Pause";
        Icon icon;
        string text;
        StreamDeckVisualState streamDeckState;
        string streamDeckTitle;

        if (_pausing)
        {
            icon = _errorIcon;
            text = "AppSupervisor - Pausing; finishing lifecycle tasks";
            streamDeckState = StreamDeckVisualState.Error;
            streamDeckTitle = "Pausing";
        }
        else if (_pausedManually)
        {
            icon = _pausedIcon;
            text = "AppSupervisor - Paused";
            streamDeckState = StreamDeckVisualState.Paused;
            streamDeckTitle = "Paused";
        }
        else if (_configurationLoadError)
        {
            icon = _errorIcon;
            text = TrayTooltipText.FormatError(
                _configurationLoadErrorTrayStatus,
                additionalErrorCount: 0
            );
            streamDeckState = StreamDeckVisualState.Error;
            streamDeckTitle = "Config error";
        }
        else if (hasRuntimeError || _hasSteamVrOfflineDevices)
        {
            icon = shutdownPending
                ? _stoppingErrorIcon
                : startupPending
                    ? _startingErrorIcon
                    : _errorIcon;
            IReadOnlyList<SteamVrOfflineDevice> offlineDevices = _steamVrOfflineDevices;
            int steamVrErrorCount = _hasSteamVrOfflineDevices
                ? Math.Max(1, offlineDevices.Count)
                : 0;
            string errorSummary = runtimeErrors.LatestSummary ??
                CreateSteamVrTrayErrorSummary(offlineDevices);
            string? activity = shutdownPending
                ? shutdownText
                : startupPending
                    ? "starting helpers"
                    : null;
            int additionalErrorCount = Math.Max(
                0,
                runtimeErrors.Count + steamVrErrorCount - 1
            );
            text = TrayTooltipText.FormatError(
                errorSummary,
                additionalErrorCount,
                activity
            );
            streamDeckState = shutdownPending
                ? StreamDeckVisualState.StoppingError
                : startupPending
                    ? StreamDeckVisualState.StartingError
                    : StreamDeckVisualState.Error;
            streamDeckTitle = activity is null ? "Error" : $"Error\n{activity}";
        }
        else if (_profiles.Count == 0 && !_steamVrMonitoringEnabled)
        {
            icon = _errorIcon;
            text = "AppSupervisor - No enabled profiles";
            streamDeckState = StreamDeckVisualState.Error;
            streamDeckTitle = "No profiles";
        }
        else if (_profiles.Count == 0)
        {
            icon = _appIcon;
            text = "AppSupervisor - Monitoring SteamVR";
            streamDeckState = StreamDeckVisualState.Idle;
            streamDeckTitle = "SteamVR";
        }
        else if (_paused)
        {
            icon = _pausedIcon;
            text = "AppSupervisor - Paused";
            streamDeckState = StreamDeckVisualState.Paused;
            streamDeckTitle = "Paused";
        }
        else if (shutdownPending)
        {
            icon = supervisionActive ? _stoppingSupervisingIcon : _stoppingIcon;
            text = supervisionActive
                ? $"AppSupervisor - Supervising; {shutdownText}"
                : $"AppSupervisor - {char.ToUpperInvariant(shutdownText[0])}{shutdownText[1..]}";
            streamDeckState = supervisionActive
                ? StreamDeckVisualState.StoppingSupervising
                : StreamDeckVisualState.Stopping;
            streamDeckTitle = "Closing";
        }
        else if (supervisionActive)
        {
            icon = startupPending ? _startingSupervisingIcon : _supervisingIcon;
            text = startupPending
                ? "AppSupervisor - Starting helpers"
                : "AppSupervisor - Supervising";
            streamDeckState = startupPending
                ? StreamDeckVisualState.StartingSupervising
                : StreamDeckVisualState.Supervising;
            streamDeckTitle = startupPending ? "Starting" : "Supervising";
        }
        else
        {
            icon = _appIcon;
            text = "AppSupervisor - Waiting for monitored applications";
            streamDeckState = StreamDeckVisualState.Idle;
            streamDeckTitle = "Waiting";
        }

        var state = new TrayStateSnapshot(
            pauseEnabled,
            pauseText,
            icon,
            text,
            streamDeckState,
            streamDeckTitle
        );

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

    /// <summary>Creates a monotonically ordered active error while holding the runtime-state lock.</summary>
    private ActiveTrayError CreateActiveTrayError(string summary)
        => new(++_trayErrorSequence, summary);

    /// <summary>Returns the most recent concrete runtime error and the total active-error count.</summary>
    private ActiveTrayErrorSnapshot GetActiveTrayErrorSnapshot()
    {
        int count = 0;
        ActiveTrayError latest = default;

        void Consider(ActiveTrayError error)
        {
            count++;

            if (count == 1 || error.Sequence > latest.Sequence)
                latest = error;
        }

        foreach (ActiveTrayError error in _activeRuntimeErrors.Values)
            Consider(error);

        foreach (ActiveTrayError error in _activeHealthErrors.Values)
            Consider(error);

        foreach (ActiveTrayError error in _reportedProfileTickErrors.Values)
            Consider(error);

        foreach (ActiveTrayError error in _reportedLifecycleTickErrors.Values)
            Consider(error);

        if (_inactiveCleanupError is ActiveTrayError inactiveCleanupError)
            Consider(inactiveCleanupError);

        return new ActiveTrayErrorSnapshot(count, count == 0 ? null : latest.Summary);
    }

    /// <summary>Describes the first currently offline SteamVR device without reverting to a generic error.</summary>
    private static string CreateSteamVrTrayErrorSummary(
        IReadOnlyList<SteamVrOfflineDevice> offlineDevices)
    {
        return offlineDevices.Count == 0
            ? "SteamVR device offline"
            : TrayTooltipText.CreateErrorSummary(
                "SteamVR device offline",
                offlineDevices[0].Name
            );
    }

    /// <summary>Applies a precomputed immutable tray snapshot on the WinForms thread.</summary>
    private void ApplyTrayState(TrayStateSnapshot state)
    {
        if (_exiting)
            return;

        bool transition = !string.Equals(_trayIcon.Text, state.Text, StringComparison.Ordinal);

        if (transition)
        {
            SupervisorLog.WriteTrace(
                $"Tray transition applying: '{_trayIcon.Text}' -> '{state.Text}'."
            );
        }

        _pauseResumeItem.Enabled = state.PauseEnabled;
        _pauseResumeItem.Text = state.PauseText;
        _trayIcon.Icon = state.Icon;
        _trayIcon.Text = state.Text;
        _streamDeckStatusClient.Publish(new StreamDeckStatusSnapshot(
            state.StreamDeckState,
            state.StreamDeckTitle,
            state.Text,
            _streamDeckStatusImages[state.StreamDeckState]
        ));

        if (transition)
            SupervisorLog.WriteTrace($"Tray transition applied: '{state.Text}'.");
    }

    /// <summary>Describes every user-visible tray value that must change as one UI update.</summary>
    private readonly record struct TrayStateSnapshot(
        bool PauseEnabled,
        string PauseText,
        Icon Icon,
        string Text,
        StreamDeckVisualState StreamDeckState,
        string StreamDeckTitle)
    {
        /// <summary>Compares stable tray values without relying on native icon-handle equality.</summary>
        public bool Matches(TrayStateSnapshot other)
        {
            return PauseEnabled == other.PauseEnabled &&
                string.Equals(PauseText, other.PauseText, StringComparison.Ordinal) &&
                ReferenceEquals(Icon, other.Icon) &&
                string.Equals(Text, other.Text, StringComparison.Ordinal) &&
                StreamDeckState == other.StreamDeckState &&
                string.Equals(StreamDeckTitle, other.StreamDeckTitle, StringComparison.Ordinal);
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

        if (_configurationEditor is { IsDisposed: false } configurationEditor)
        {
            try
            {
                await configurationEditor.StopHelperTestForSupervisorExitAsync();
            }
            catch (Exception exception)
            {
                SupervisorLog.WriteError(
                    "The test helper could not be closed before AppSupervisor exited.",
                    exception
                );
            }
        }

        CloseAllWindows();
        SaveVerifiedConfigurationBackup();
        Application.ApplicationExit -= ApplicationExiting;
        SystemEvents.PowerModeChanged -= SystemPowerModeChanged;
        _paused = true;
        _pausing = false;
        _pauseDrainStarted = false;
        _monitorTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _lifecycleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _ensureClosedTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _twitchAuthorizationTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

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
        _supervisorApi.Dispose();
        await _streamDeckStatusClient.DisposeAsync();
        await Task.Run(_notificationService.Dispose);

        _ensureClosedTimer.Dispose();
        _twitchAuthorizationTimer.Dispose();
        _lifecycleTimer.Dispose();
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
