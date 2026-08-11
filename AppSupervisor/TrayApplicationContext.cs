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
    private readonly Form _dialogOwner;
    private readonly string _configPath;
    private readonly System.Windows.Forms.Timer _monitorTimer;
    private readonly System.Windows.Forms.Timer _startupTimer;
    private readonly System.Windows.Forms.Timer _ensureClosedTimer;
    private readonly ToolStripMenuItem _pauseResumeItem;
    private readonly NotificationService _notificationService;

    private List<SupervisorProfileConfig> _config = [];
    private List<SupervisorProfile> _profiles = [];
    private ApplicationUsageRegistry _applicationUsageRegistry = new();
    private readonly HashSet<SupervisorProfile> _reportedProfileTickErrors = [];
    private readonly HashSet<SupervisorProfile> _reportedStartupTickErrors = [];
    private bool _paused;
    private bool _pausedManually;
    private bool _configurationError;
    private bool _runtimeError;
    private bool _hasValidConfiguration;
    private bool _configurationEditorOpen;
    private bool _exiting;

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

        var contextMenu = new ContextMenuStrip();

        _pauseResumeItem = new ToolStripMenuItem("Pause");
        _pauseResumeItem.Click += TogglePause;

        contextMenu.Items.Add("Configure...", null, OpenConfigurationEditor);
        contextMenu.Items.Add(_pauseResumeItem);
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

        _monitorTimer = new System.Windows.Forms.Timer
        {
            Interval = 1000
        };

        Application.ApplicationExit += ApplicationExiting;
        _startupTimer = new System.Windows.Forms.Timer
        {
            Interval = 100
        };
        _ensureClosedTimer = new System.Windows.Forms.Timer
        {
            Interval = 5 * 60 * 1000
        };
        _monitorTimer.Tick += MonitorTimerTick;
        _monitorTimer.Start();

        _startupTimer.Tick += StartupTimerTick;
        _startupTimer.Start();
        _ensureClosedTimer.Tick += EnsureClosedTimerTick;
        _ensureClosedTimer.Start();
        LoadConfiguration(showNotification: false);
        Application.Idle += ApplicationBecameIdle;
    }

    /// <summary>
    /// Runs one update for every supervisor profile unless supervision is paused.
    /// </summary>
    /// <param name="sender">The shared WinForms timer.</param>
    /// <param name="e">The timer event data.</param>
    private void MonitorTimerTick(object? sender, EventArgs e)
    {
        if (_paused)
            return;

        foreach (var profile in _profiles)
        {
            try
            {
                bool stateChanged = profile.Update();
                _reportedProfileTickErrors.Remove(profile);

                if (!stateChanged)
                    continue;

                PublishNotification(
                    NotificationSeverity.Information,
                    "AppSupervisor",
                    $"{profile.Name}: {profile.TriggerDisplayName} is now " +
                    (profile.TriggerActive ? "running." : "stopped."),
                    profile.NotificationTargets
                );
            }
            catch (Exception ex)
            {
                _runtimeError = true;
                UpdateTrayState();

                if (!_reportedProfileTickErrors.Add(profile))
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

    /// <summary>Starts one nonblocking five-minute cleanup sweep for opted-in inactive helper applications.</summary>
    /// <param name="sender">The dedicated cleanup timer.</param>
    /// <param name="e">The timer event data.</param>
    private void EnsureClosedTimerTick(object? sender, EventArgs e)
    {
        if (_paused)
            return;

        _applicationUsageRegistry.Sweep();
    }

    /// <summary>Advances dependency and millisecond-delay startup sequences without running full supervision polling.</summary>
    /// <param name="sender">The lightweight WinForms startup timer.</param>
    /// <param name="e">The timer event data.</param>
    private void StartupTimerTick(object? sender, EventArgs e)
    {
        if (_paused)
            return;

        DateTime nowUtc = DateTime.UtcNow;

        foreach (SupervisorProfile profile in _profiles)
        {
            try
            {
                profile.AdvanceStartup(nowUtc);
                _reportedStartupTickErrors.Remove(profile);
            }
            catch (Exception ex)
            {
                _runtimeError = true;
                UpdateTrayState();

                if (!_reportedStartupTickErrors.Add(profile))
                    continue;

                PublishNotification(
                    NotificationSeverity.Error,
                    "Supervision error",
                    $"{profile.Name}\nUnexpected startup sequencing failure: {ex.Message}",
                    profile.NotificationTargets
                );
            }
        }
    }
    /// <summary>
    /// Toggles supervision without starting, closing, or otherwise altering managed resources.
    /// </summary>
    /// <param name="sender">The Pause or Resume menu item.</param>
    /// <param name="e">The menu-click event data.</param>
    private void TogglePause(object? sender, EventArgs e)
    {
        _paused = !_paused;
        _pausedManually = _paused;

        if (_paused)
        {
            _applicationUsageRegistry.SuspendCleanup();

            foreach (SupervisorProfile profile in _profiles)
                profile.SuspendMonitoring();
        }
        else
        {
            _ensureClosedTimer.Stop();
            _ensureClosedTimer.Start();
        }

        UpdateTrayState();
    }

    /// <summary>
    /// Builds a complete replacement configuration and swaps it into use only when loading and validation succeed.
    /// </summary>
    /// <param name="showNotification">Whether to notify after a successful manual reload.</param>
    private void LoadConfiguration(bool showNotification)
    {
        var newProfiles = new List<SupervisorProfile>();
        var newApplicationUsageRegistry = new ApplicationUsageRegistry();
        List<SupervisorProfileConfig> newConfig;

        try
        {
            newConfig = ConfigLoader.Load(_configPath);

            foreach (var profileConfig in newConfig.Where(profile => profile.Enabled))
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
                        )
                );
                profile.ResourceRestarted += OnResourceRestarted;
                profile.ErrorOccurred += OnSupervisionError;
                profile.ResourceNotificationRequested += OnResourceNotificationRequested;
                newProfiles.Add(profile);
                newApplicationUsageRegistry.RegisterProfile(profileConfig, profile);
            }

            newApplicationUsageRegistry.CompleteRegistration();
            newApplicationUsageRegistry.CleanupFailed += OnInactiveApplicationCleanupFailed;
        }
        catch (Exception ex)
        {
            foreach (var newProfile in newProfiles)
                newProfile.Dispose();

            newApplicationUsageRegistry.Dispose();
            HandleConfigurationLoadFailure(ex);
            return;
        }

        List<SupervisorProfile> oldProfiles = _profiles;
        ApplicationUsageRegistry oldApplicationUsageRegistry = _applicationUsageRegistry;

        _config = newConfig;
        _profiles = newProfiles;
        _applicationUsageRegistry = newApplicationUsageRegistry;
        _hasValidConfiguration = true;
        _configurationError = false;
        _runtimeError = false;
        _activeHealthErrors.Clear();
        _reportedProfileTickErrors.Clear();
        UpdateTrayState();
        _reportedStartupTickErrors.Clear();

        foreach (SupervisorProfile oldProfile in oldProfiles)
            oldProfile.Dispose();

        oldApplicationUsageRegistry.CleanupFailed -= OnInactiveApplicationCleanupFailed;
        oldApplicationUsageRegistry.Dispose();

        foreach (SupervisorProfile newProfile in _profiles)
            newProfile.InitializeResources();

        _ensureClosedTimer.Stop();
        _ensureClosedTimer.Start();

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
        _runtimeError = true;
        UpdateTrayState();

        PublishNotification(
            NotificationSeverity.Error,
            "Inactive helper close failed",
            $"{displayName}\n{message}",
            targets
        );
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
        _runtimeError = true;
        UpdateTrayState();

        PublishNotification(
            NotificationSeverity.Error,
            "Supervision error",
            $"{profile.Name} - {resource.DisplayName}\n{message}",
            resource.NotificationTargets
        );
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
    private void ApplicationBecameIdle(object? sender, EventArgs e)
    {
        Application.Idle -= ApplicationBecameIdle;

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
        _pauseResumeItem.Text = _paused
            ? "Resume"
            : "Pause";

        if (_pausedManually)
        {
            _trayIcon.Icon = _pausedIcon;
            _trayIcon.Text = "AppSupervisor - Paused";
        }
        else if (_configurationError)
        {
            _trayIcon.Icon = _errorIcon;
            _trayIcon.Text = "AppSupervisor - Configuration error";
        }
        else if (_runtimeError || _activeHealthErrors.Count > 0)
        {
            _trayIcon.Icon = _errorIcon;
            _trayIcon.Text = "AppSupervisor - Supervision error";
        }
        else if (_profiles.Count == 0)
        {
            _trayIcon.Icon = _errorIcon;
            _trayIcon.Text = "AppSupervisor - No enabled profiles";
        }
        else if (_paused)
        {
            _trayIcon.Icon = _pausedIcon;
            _trayIcon.Text = "AppSupervisor - Paused";
        }
        else if (_profiles.Any(profile => profile.TriggerActive))
        {
            _trayIcon.Icon = _supervisingIcon;
            _trayIcon.Text = "AppSupervisor - Supervising";
        }
        else
        {
            _trayIcon.Icon = _appIcon;
            _trayIcon.Text = "AppSupervisor - Waiting for monitored applications";
        }
    }

    /// <summary>
    /// Stops AppSupervisor, cancels notifications and runtime work, releases tray resources, and leaves managed resources untouched.
    /// </summary>
    /// <param name="sender">The Exit menu item.</param>
    /// <param name="e">The menu-click event data.</param>
    private void Exit(object? sender, EventArgs e)
    {
        if (_exiting)
            return;

        _exiting = true;
        Application.Idle -= ApplicationBecameIdle;
        CloseAllWindows();
        SaveVerifiedConfigurationBackup();
        Application.ApplicationExit -= ApplicationExiting;
        _monitorTimer.Stop();
        _startupTimer.Stop();
        _ensureClosedTimer.Stop();
        _ensureClosedTimer.Tick -= EnsureClosedTimerTick;
        _ensureClosedTimer.Dispose();
        _startupTimer.Dispose();
        _monitorTimer.Dispose();
        _applicationUsageRegistry.CleanupFailed -= OnInactiveApplicationCleanupFailed;
        _applicationUsageRegistry.Dispose();

        foreach (var profile in _profiles)
            profile.Dispose();

        _profiles.Clear();
        _notificationService.Dispose();

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _errorIcon.Dispose();
        _pausedIcon.Dispose();
        _appIcon.Dispose();
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
