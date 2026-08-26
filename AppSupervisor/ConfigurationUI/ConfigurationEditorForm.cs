using AppSupervisor.Configuration;
using AppSupervisor.Core;
using AppSupervisor.Notifications;
using AppSupervisor.HomeAssistant;
using AppSupervisor.Obs;
using AppSupervisor.StreamDeck;
using AppSupervisor.WindowsAudio;

using AppSupervisor.SteamVr;
using AppSupervisor.ServiceControl;


namespace AppSupervisor.ConfigurationUI;

/// <summary>
/// Provides a structured editor for supervisor profiles, helper applications, services, health checks, and notifications.
/// </summary>
public sealed partial class ConfigurationEditorForm : Form
{
    private readonly string _configPath;
    private readonly AppSupervisorConfig _configuration;
    private readonly List<SupervisorProfileConfig> _profiles;

    private readonly Func<CancellationToken, Task<IReadOnlyList<InstalledServiceInfo>>> _serviceCatalogLoader;
    private readonly Func<CancellationToken, Task<IReadOnlyList<AudioEndpointSnapshot>>> _audioEndpointLoader;
    private readonly Action<InstalledServiceInfo> _automaticServiceWarning;

    private IReadOnlyList<InstalledServiceInfo> _installedServices = [];

    private readonly ComboBox _profileSelector = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 340
    };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly Label _statusLabel = new()
    {
        AutoSize = true,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private readonly CheckBox _profileEnabled = new() { Text = "Profile enabled", AutoSize = true };
    private readonly TextBox _profileName = new() { Dock = DockStyle.Fill };
    private readonly TextBox _monitorProcess = new() { Dock = DockStyle.Fill };
    private readonly NullableSecondsControl _closeTimeout = new();
    private readonly NullableSecondsControl _restartTimeout = new();

    private readonly CheckBox _applicationEnabled = new()
    {
        Text = "Helper enabled",
        AutoSize = true
    };
    private readonly TextBox _applicationPath = new() { Dock = DockStyle.Fill };
    private readonly TextBox _applicationAppUri = new() { Dock = DockStyle.Fill };
    private readonly TextBox _applicationArguments = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _applicationRestart = new()
    {
        Text = "Restart after unexpected exit",
        AutoSize = true
    };
    private readonly CheckBox _applicationEnsureClosedUntilNeeded = new()
    {
        Text = "Ensure closed until needed",
        AutoSize = true
    };
    private readonly CheckBox _applicationLeaveRunning = new()
    {
        Text = "Leave helper running after monitored app closes",
        AutoSize = true
    };
    private readonly CheckBox _applicationMinimize = new()
    {
        Text = "Minimize windows after starting",
        AutoSize = true
    };
    private readonly CheckBox _applicationForceKill = new()
    {
        Text = "Allow force-kill after all graceful close attempts fail",
        AutoSize = true
    };
    private readonly CheckBox _applicationMonitorResponsiveness = new()
    {
        Text = "Monitor application responsiveness",
        AutoSize = true
    };
    private readonly NotificationTargetsControl _applicationNotifications = new();
    private readonly ListBox _healthCheckList = new()
    {
        Dock = DockStyle.Fill,
        DrawMode = DrawMode.OwnerDrawFixed,
        IntegralHeight = false
    };
    private readonly Panel _applicationEditorPanel = new() { Dock = DockStyle.Fill };

    private readonly CheckBox _serviceEnabled = new()
    {
        Text = "Service enabled",
        AutoSize = true
    };
    private readonly ComboBox _serviceName = new()

    {

        Dock = DockStyle.Fill,

        DropDownStyle = ComboBoxStyle.DropDownList,

        FormattingEnabled = true

    };
    private readonly CheckBox _serviceRestart = new()
    {
        Text = "Restart after unexpected stop",
        AutoSize = true
    };
    private readonly NotificationTargetsControl _serviceNotifications = new();
    private readonly Panel _serviceEditorPanel = new() { Dock = DockStyle.Fill };

    private DateTime? _loadedWriteTimeUtc;
    private string? _loadError;
    private bool _loadingControls;
    private string _unchangedConfigurationState = "";
    private bool _hasUnsavedChanges;
    private bool _closeWithoutUnsavedChangesPrompt;
    private Button _validateButton = null!;
    private Button _saveButton = null!;
    private Button _exportProfileButton = null!;

    /// <summary>Loads a detached configuration document and constructs all editor pages.</summary>
    /// <param name="configPath">The active config.json path.</param>
    public ConfigurationEditorForm(string configPath)

        : this(
            configPath,
            cancellationToken => Task.Run(
                InstalledServiceCatalog.LoadThirdPartyServices,
                cancellationToken
            ),
            notificationPublisher: null
        )

    {

    }



    /// <summary>Loads a detached configuration document using the supplied installed-service catalog provider.</summary>

    /// <param name="configPath">The active config.json path.</param>

    /// <param name="serviceCatalogLoader">The function that discovers selectable third-party services.</param>

    /// <param name="notificationPublisher">The optional live notification publishing callback used by test actions.</param>

    internal ConfigurationEditorForm(

        string configPath,

        Func<CancellationToken, Task<IReadOnlyList<InstalledServiceInfo>>> serviceCatalogLoader,
        Action<SupervisorNotification>? notificationPublisher,
        Func<CancellationToken, Task<SteamVrSnapshot>>? steamVrDeviceLoader = null,
        Func<HomeAssistantIntegrationConfig, CancellationToken, Task<HomeAssistantCatalog>>?
            homeAssistantCatalogLoader = null,
        Func<ObsIntegrationConfig, CancellationToken, Task<ObsCatalog>>?
            obsCatalogLoader = null,
        Func<CancellationToken, Task<IReadOnlyList<AudioEndpointSnapshot>>>?
            audioEndpointLoader = null,
        Func<CancellationToken, Task<IReadOnlyList<StreamDeckMcpAction>>>?
            streamDeckActionLoader = null,
        Func<StreamDeckResourceConfig, CancellationToken, Task>?
            streamDeckActionExecutor = null,
        TimeSpan? streamDeckSwitchTestDuration = null,
        Action<InstalledServiceInfo>? automaticServiceWarning = null,
        IHelperTestController? helperTestController = null,
        Func<ConfigurationRuntimeStatusSnapshot>? runtimeStatusReader = null,
        IProfileTransferInteraction? profileTransferInteraction = null)

    {

        _configPath = Path.GetFullPath(configPath);

        _serviceCatalogLoader = serviceCatalogLoader;
        _notificationPublisher = notificationPublisher;
        _steamVrDeviceLoader = steamVrDeviceLoader ?? LoadSteamVrDevicesAsync;
        _homeAssistantCatalogLoader = homeAssistantCatalogLoader ??
            HomeAssistantClient.LoadCatalogAsync;
        _obsCatalogLoader = obsCatalogLoader ?? ObsWebSocketClient.LoadCatalogAsync;
        _audioEndpointLoader = audioEndpointLoader ?? (cancellationToken => Task.Run(
            () => (IReadOnlyList<AudioEndpointSnapshot>)new WindowsAudioController()
                .GetActiveEndpoints(),
            cancellationToken
        ));
        _streamDeckActionLoader = streamDeckActionLoader ??
            StreamDeckMcpClient.Shared.LoadActionsAsync;
        _streamDeckActionExecutor = streamDeckActionExecutor ??
            StreamDeckMcpClient.Shared.ExecuteActionAsync;
        _streamDeckSwitchTestDuration = streamDeckSwitchTestDuration ?? TimeSpan.FromSeconds(5);
        _automaticServiceWarning = automaticServiceWarning ?? ShowAutomaticServiceWarning;
        _helperTestController = helperTestController;
        _runtimeStatusReader = runtimeStatusReader;
        _profileTransferInteraction = profileTransferInteraction ?? new ProfileTransferInteraction();
        (_configuration, _loadError) = LoadConfigurationForEditing(_configPath);
        _profiles = _configuration.Profiles;
        _loadedWriteTimeUtc = GetWriteTimeUtc(_configPath);

        Text = "AppSupervisor Configuration";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 680);
        Size = new Size(1180, 800);
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        Controls.Add(_tabs);
        Controls.Add(BuildHeader());
        Controls.Add(BuildFooter());
        _tabs.TabPages.Add(BuildProfilePage());
        _tabs.TabPages.Add(BuildResourcesPage());
        _tabs.TabPages.Add(BuildIntegrationsPage());
        _tabs.TabPages.Add(BuildDiagnosticLogsPage());
        WireEvents();
        InitializeHelperTesting();
        InitializeRuntimeStatus();
        InitializeDiagnosticLogs();

        BeginRefreshInstalledServices(showErrors: false);

        BindProfileSelector();
        _unchangedConfigurationState = SerializeEditingState();
        UpdateStatus();
    }

    /// <summary>Builds the supervisor-profile selector and add, duplicate, and remove commands.</summary>
    /// <returns>The docked editor header.</returns>
    private Control BuildHeader()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 54,
            Padding = new Padding(12, 10, 12, 8),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        panel.Controls.Add(new Label
        {
            Text = "Profile:",
            AutoSize = true,
            Margin = new Padding(0, 7, 8, 0)
        });
        panel.Controls.Add(_profileSelector);
        panel.Controls.Add(CreateButton("Add profile", AddProfileClicked));
        panel.Controls.Add(CreateButton("Duplicate", DuplicateProfileClicked));
        panel.Controls.Add(CreateButton("Remove", RemoveProfileClicked));
        panel.Controls.Add(CreateButton("Import profile...", ImportProfileClicked));
        _exportProfileButton = CreateButton("Export profile...", ExportProfileClicked);
        panel.Controls.Add(_exportProfileButton);
        return panel;
    }

    /// <summary>Builds validation, Save &amp; Apply, and cancel commands with status feedback.</summary>
    /// <returns>The docked editor footer.</returns>
    private Control BuildFooter()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 62,
            Padding = new Padding(12, 10, 12, 10),
            ColumnCount = 2
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty
        };
        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(72, 27),
            Margin = new Padding(6, 0, 0, 0),
            Padding = new Padding(8, 0, 8, 0)
        };
        _saveButton = CreateButton("Save && Apply", SaveClicked);
        _validateButton = CreateButton("Validate", ValidateClicked);
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(_saveButton);
        buttons.Controls.Add(_validateButton);
        panel.Controls.Add(_statusLabel, 0, 0);
        panel.Controls.Add(buttons, 1, 0);
        AcceptButton = _saveButton;
        CancelButton = cancelButton;
        return panel;
    }

    /// <summary>Builds the selected supervisor profile's identity, trigger, and timeout page.</summary>
    /// <returns>The Profile tab page.</returns>
    private TabPage BuildProfilePage()
    {
        var page = new TabPage("Profile settings") { Padding = new Padding(16) };
        TableLayoutPanel layout = CreateEditorTable();
        AddEditorRow(layout, "", _profileEnabled);
        AddEditorRow(layout, "Name", _profileName);

        var monitorPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            Margin = Padding.Empty
        };
        monitorPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        monitorPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        monitorPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _monitorProcess.Margin = Padding.Empty;
        monitorPanel.Controls.Add(_monitorProcess, 0, 0);
        monitorPanel.Controls.Add(CreateButton("Browse...", BrowseMonitorProcessClicked), 1, 0);
        monitorPanel.Controls.Add(CreateButton("Pick running...", PickMonitorProcessClicked), 2, 0);
        AddEditorRow(layout, "Monitor process", monitorPanel);
        AddEditorRow(layout, "Close timeout", _closeTimeout);
        AddEditorRow(layout, "Restart timeout", _restartTimeout);
        AddEditorRow(layout, "", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(720, 0),
            ForeColor = SystemColors.GrayText,
            Text = "The profile is active while the monitor process is running. Resources start in order, remain supervised while it is active, and close after the configured close timeout when it stops. Add a Delay as the first resource when startup should wait."
        });
        page.Controls.Add(layout);
        return page;
    }

    /// <summary>Builds lifecycle, notification, and health-check fields for the selected application.</summary>
    /// <returns>The application editor panel.</returns>
    private Control BuildApplicationEditor()
    {
        _applicationEditorPanel.Padding = new Padding(14);
        var scrolling = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        TableLayoutPanel layout = CreateEditorTable();
        AddEditorRow(layout, "", _applicationEnabled);

        var pathPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            Margin = Padding.Empty
        };
        pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathPanel.Controls.Add(_applicationPath, 0, 0);
        _applicationPath.Margin = Padding.Empty;
        pathPanel.Controls.Add(CreateButton("Browse...", BrowseApplicationClicked), 1, 0);
        pathPanel.Controls.Add(CreateButton("Pick running...", PickApplicationProcessClicked), 2, 0);
        AddEditorRow(layout, "Executable", pathPanel);
        AddEditorRow(layout, "App URI", BuildAppUriEditor());
        AddEditorRow(layout, "Arguments", _applicationArguments);
        AddEditorRow(layout, "", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Use Pick Steam or Pick Store to fill both the app URI and real executable automatically. The executable remains the process AppSupervisor monitors. Arguments apply only to direct executable launches."
        });
        AddEditorRow(layout, "Restart", _applicationRestart);
        AddEditorRow(layout, "When inactive", _applicationLeaveRunning);
        AddEditorRow(layout, "When inactive", _applicationEnsureClosedUntilNeeded);
        AddEditorRow(layout, "", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Leave running skips the normal close when this profile becomes inactive. Ensure closed checks every five minutes and closes the helper only when no enabled profile using the same executable needs it. These options are mutually exclusive."
        });
        AddEditorRow(layout, "Responsiveness", _applicationMonitorResponsiveness);
        AddEditorRow(layout, "", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Responsiveness monitoring checks hidden and visible helper windows. After three failed checks, AppSupervisor gracefully restarts the helper. Helpers without an owned window are not treated as frozen."
        });
        AddEditorRow(layout, "After launch", _applicationMinimize);
        AddEditorRow(layout, "Close fallback", _applicationForceKill);
        AddEditorRow(layout, "", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Force-kill is intentionally disabled by default. Without it, AppSupervisor reports an error when graceful close attempts fail and leaves the process running."
        });
        AddEditorRow(layout, "Test", BuildHelperTestPanel());
        AddEditorRow(layout, "Notifications", BuildNotificationTestPanel(
            _applicationNotifications,
            TestApplicationNotificationClicked
        ));
        AddEditorRow(layout, "Startup macros", BuildStartupMacroPanel(), alignTop: true);
        AddEditorRow(layout, "Health checks", BuildHealthCheckPanel(), alignTop: true);
        scrolling.Controls.Add(layout);
        _applicationEditorPanel.Controls.Add(scrolling);
        return _applicationEditorPanel;
    }

    /// <summary>Builds the selected application's health-check list and add/edit/remove commands.</summary>
    /// <returns>The nested health-check panel.</returns>
    private Control BuildHealthCheckPanel()
    {
        _healthCheckList.ItemHeight =
            ConfigurationIconListRenderer.GetItemHeight(_healthCheckList);
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Height = 230,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        buttons.Controls.Add(CreateButton("Add check", AddHealthCheckClicked));
        buttons.Controls.Add(CreateButton("Edit", EditHealthCheckClicked));
        buttons.Controls.Add(CreateButton("Remove", RemoveHealthCheckClicked));
        buttons.Controls.Add(CreateButton("Test check", TestHealthCheckClicked));
        buttons.Controls.Add(CreateButton("Test notification", TestHealthNotificationClicked));
        panel.Controls.Add(_healthCheckList, 0, 0);
        panel.Controls.Add(buttons, 0, 1);
        return panel;
    }

    /// <summary>Builds lifecycle and notification fields for the selected Windows service.</summary>
    /// <returns>The service editor panel.</returns>
    private Control BuildServiceEditor()
    {
        _serviceEditorPanel.Padding = new Padding(14);
        TableLayoutPanel layout = CreateEditorTable();
        AddEditorRow(layout, "", _serviceEnabled);
        AddEditorRow(layout, "Installed service", BuildServiceSelector());
        AddEditorRow(layout, "Restart", _serviceRestart);
        AddEditorRow(layout, "Notifications", BuildNotificationTestPanel(
            _serviceNotifications,
            TestServiceNotificationClicked
        ));
        AddEditorRow(layout, "", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Only non-Microsoft Win32 services are shown. Microsoft/Windows-provided services are filtered using executable publisher and Windows-hosted binary metadata. AppSupervisor enforces Manual startup during initialization."
        });
        _serviceEditorPanel.Controls.Add(layout);
        return _serviceEditorPanel;
    }

    /// <summary>Connects model selection, field changes, list formatting, and double-click editing.</summary>
    private void WireEvents()
    {
        _profileSelector.FormattingEnabled = true;
        _resourceList.FormattingEnabled = true;
        _healthCheckList.FormattingEnabled = true;
        _profileSelector.SelectedIndexChanged += ProfileSelectionChanged;
        _profileSelector.Format += ProfileSelectorFormat;
        _resourceList.SelectedIndexChanged += ResourceSelectionChanged;
        _resourceList.Format += ResourceListFormat;
        _resourceList.DrawItem += ResourceListDrawItem;
        _resourceList.MouseDown += ResourceListMouseDown;
        _resourceList.MouseMove += ResourceListMouseMove;
        _resourceList.MouseUp += ResourceListMouseUp;
        _healthCheckList.Format += HealthCheckListFormat;
        _healthCheckList.DrawItem += HealthCheckListDrawItem;
        _healthCheckList.DoubleClick += EditHealthCheckClicked;

        _profileEnabled.CheckedChanged += ProfileFieldChanged;
        _profileName.TextChanged += ProfileFieldChanged;
        _monitorProcess.TextChanged += ProfileFieldChanged;
        _closeTimeout.ValueChanged += ProfileFieldChanged;
        _restartTimeout.ValueChanged += ProfileFieldChanged;

        _resourceDependency.SelectedIndexChanged += ResourceStartupFieldChanged;
        _resourceDependency.DrawItem += ResourceDependencyDrawItem;

        _applicationEnabled.CheckedChanged += ApplicationFieldChanged;
        _applicationPath.TextChanged += ApplicationFieldChanged;
        _applicationAppUri.TextChanged += ApplicationFieldChanged;
        _applicationArguments.TextChanged += ApplicationFieldChanged;
        _applicationRestart.CheckedChanged += ApplicationFieldChanged;
        _applicationEnsureClosedUntilNeeded.CheckedChanged += ApplicationFieldChanged;
        _applicationLeaveRunning.CheckedChanged += ApplicationFieldChanged;
        _applicationMinimize.CheckedChanged += ApplicationFieldChanged;
        _applicationMonitorResponsiveness.CheckedChanged += ApplicationFieldChanged;
        _applicationForceKill.CheckedChanged += ApplicationFieldChanged;
        _applicationNotifications.TargetsChanged += ApplicationFieldChanged;

        _serviceEnabled.CheckedChanged += ServiceFieldChanged;
        _serviceName.SelectedIndexChanged += ServiceFieldChanged;

        _serviceName.Format += ServiceSelectorFormat;
        _serviceRestart.CheckedChanged += ServiceFieldChanged;
        _serviceNotifications.TargetsChanged += ServiceFieldChanged;
    }

    /// <summary>Repopulates the supervisor-profile selector while preserving a preferred selection when possible.</summary>
    /// <param name="preferred">The profile to select, or null to select the first profile.</param>
    private void BindProfileSelector(SupervisorProfileConfig? preferred = null)
    {
        _loadingControls = true;

        try
        {
            _profileSelector.Items.Clear();

            foreach (SupervisorProfileConfig profile in _profiles)
                _profileSelector.Items.Add(profile);

            if (preferred is not null && _profileSelector.Items.Contains(preferred))
                _profileSelector.SelectedItem = preferred;
            else if (_profileSelector.Items.Count > 0)
                _profileSelector.SelectedIndex = 0;
        }
        finally
        {
            _loadingControls = false;
        }

        _exportProfileButton.Enabled = _profileSelector.SelectedItem is SupervisorProfileConfig;
        LoadSelectedProfile();
    }

    /// <summary>Loads the selected profile's fields and child collections into the editor.</summary>
    private void LoadSelectedProfile()
    {
        SupervisorProfileConfig? profile = SelectedProfile;
        _loadingControls = true;

        try
        {
            _tabs.Enabled = true;
            _profileEnabled.Checked = profile?.Enabled ?? false;
            _profileName.Text = profile?.Name ?? "";
            _monitorProcess.Text = profile?.MonitorProcess ?? "";
            _closeTimeout.Value = profile?.CloseTimeoutSeconds;
            _restartTimeout.Value = profile?.RestartTimeoutSeconds;
            BindResourceList(profile);
        }
        finally
        {
            _loadingControls = false;
        }

        LoadSelectedResource();
        UpdateStatus();
        _helperTestCanStart = false;
        BeginRefreshHelperTestAvailability();
    }

    /// <summary>Loads the selected application's lifecycle, target, and health-check controls.</summary>
    private void LoadSelectedApplication()
    {
        ManagedApplicationConfig? application = SelectedApplication;
        _loadingControls = true;

        try
        {
            bool available = application is not null;
            _applicationEditorPanel.Enabled = available;
            _applicationEnabled.Checked = application?.Enabled ?? false;
            _applicationPath.Text = application?.Path ?? "";
            _applicationAppUri.Text = application?.AppUri ?? "";
            _applicationArguments.Enabled = string.IsNullOrWhiteSpace(_applicationAppUri.Text);
            _applicationArguments.Text = application?.Arguments ?? "";
            _applicationRestart.Checked = application?.Restart ?? true;
            _applicationEnsureClosedUntilNeeded.Checked =
                application?.EnsureClosedUntilNeeded ?? false;
            _applicationLeaveRunning.Checked =
                application?.LeaveRunningAfterProfileStops ?? false;
            _applicationEnsureClosedUntilNeeded.Enabled =
                !(_applicationLeaveRunning.Checked && available);
            _applicationMinimize.Checked = application?.MinimizeAfterStart ?? false;
            _applicationMonitorResponsiveness.Checked = application?.MonitorResponsiveness ?? false;
            _applicationForceKill.Checked = application?.ForceKillAfterCloseFailure ?? false;
            _applicationNotifications.LoadTargets(
                application?.Notifications.Target ?? []
            );
            BindStartupMacroList(application);
            BindHealthCheckList(application);
        }
        finally
        {
            _loadingControls = false;
        }

        BeginRefreshHelperTestAvailability();
    }

    /// <summary>Loads the selected service's lifecycle and notification controls.</summary>
    private void LoadSelectedService()
    {
        ManagedServiceConfig? service = SelectedService;
        _loadingControls = true;

        try
        {
            bool available = service is not null;
            _serviceEditorPanel.Enabled = available;
            _serviceEnabled.Checked = service?.Enabled ?? false;
            BindServiceSelector(service?.ServiceName);
            _serviceRestart.Checked = service?.Restart ?? true;
            _serviceNotifications.LoadTargets(service?.Notifications.Target ?? []);
        }
        finally
        {
            _loadingControls = false;
        }
    }

    /// <summary>Rebuilds the selected application's health-check list.</summary>
    /// <param name="application">The selected application, or null when none exists.</param>
    /// <param name="preferred">The health check to keep selected.</param>
    private void BindHealthCheckList(
        ManagedApplicationConfig? application,
        HealthCheckConfig? preferred = null)
    {
        _healthCheckList.Items.Clear();

        if (application is null)
            return;

        foreach (HealthCheckConfig healthCheck in application.HealthChecks)
            _healthCheckList.Items.Add(healthCheck);

        if (preferred is not null && _healthCheckList.Items.Contains(preferred))
            _healthCheckList.SelectedItem = preferred;
        else if (_healthCheckList.Items.Count > 0)
            _healthCheckList.SelectedIndex = 0;
    }

    /// <summary>Updates the selected profile model from its controls.</summary>
    /// <param name="sender">The changed profile control.</param>
    /// <param name="e">The change event data.</param>
    private void ProfileFieldChanged(object? sender, EventArgs e)
    {
        if (_loadingControls || SelectedProfile is not SupervisorProfileConfig profile)
            return;

        profile.Enabled = _profileEnabled.Checked;
        profile.Name = _profileName.Text;
        profile.MonitorProcess = _monitorProcess.Text;
        profile.CloseTimeoutSeconds = _closeTimeout.Value;
        profile.RestartTimeoutSeconds = _restartTimeout.Value;
        _profileSelector.Refresh();
        UpdateStatus();
    }

    /// <summary>Updates the selected application model from its controls.</summary>
    /// <param name="sender">The changed application control.</param>
    /// <param name="e">The change event data.</param>
    private void ApplicationFieldChanged(object? sender, EventArgs e)
    {
        if (_loadingControls || SelectedApplication is not ManagedApplicationConfig application)
            return;

        if (ReferenceEquals(sender, _applicationLeaveRunning) &&
            _applicationLeaveRunning.Checked)
        {
            _applicationEnsureClosedUntilNeeded.Checked = false;
        }
        else if (ReferenceEquals(sender, _applicationEnsureClosedUntilNeeded) &&
            _applicationEnsureClosedUntilNeeded.Checked)
        {
            _applicationLeaveRunning.Checked = false;
        }

        _applicationEnsureClosedUntilNeeded.Enabled = !_applicationLeaveRunning.Checked;

        application.Enabled = _applicationEnabled.Checked;
        application.Path = _applicationPath.Text;
        application.AppUri = _applicationAppUri.Text;
        _applicationArguments.Enabled = string.IsNullOrWhiteSpace(_applicationAppUri.Text);
        application.Arguments = _applicationArguments.Text;
        application.Restart = _applicationRestart.Checked;
        application.EnsureClosedUntilNeeded = _applicationEnsureClosedUntilNeeded.Checked;
        application.LeaveRunningAfterProfileStops = _applicationLeaveRunning.Checked;
        application.MinimizeAfterStart = _applicationMinimize.Checked;
        application.MonitorResponsiveness = _applicationMonitorResponsiveness.Checked;
        application.ForceKillAfterCloseFailure = _applicationForceKill.Checked;
        application.Notifications.Target = [.. _applicationNotifications.SelectedTargets];
        _resourceList.Refresh();
        UpdateStatus();
    }

    /// <summary>Updates the selected service model from its controls.</summary>
    /// <param name="sender">The changed service control.</param>
    /// <param name="e">The change event data.</param>
    private void ServiceFieldChanged(object? sender, EventArgs e)
    {
        if (_loadingControls || SelectedService is not ManagedServiceConfig service)
            return;

        service.Enabled = _serviceEnabled.Checked;
        if (_serviceName.SelectedItem is InstalledServiceInfo installedService)
        {
            if (!string.Equals(
                service.ServiceName,
                installedService.ServiceName,
                StringComparison.OrdinalIgnoreCase))
            {
                WarnIfAutomaticService(installedService);
            }
            service.ServiceName = installedService.ServiceName;
        }

        service.Restart = _serviceRestart.Checked;
        service.Notifications.Target = [.. _serviceNotifications.SelectedTargets];
        _resourceList.Refresh();
        UpdateStatus();
    }

    /// <summary>Loads a newly selected supervisor profile.</summary>
    /// <param name="sender">The profile selector.</param>
    /// <param name="e">The selection-change event data.</param>
    private void ProfileSelectionChanged(object? sender, EventArgs e)
    {
        if (!_loadingControls)
            LoadSelectedProfile();
    }

    /// <summary>Formats one profile selector item using its name and disabled state.</summary>
    /// <param name="sender">The profile selector.</param>
    /// <param name="e">The list formatting event data.</param>
    private void ProfileSelectorFormat(object? sender, ListControlConvertEventArgs e)
    {
        if (e.ListItem is SupervisorProfileConfig profile)
            e.Value = $"{DisplayName(profile.Name, "Unnamed profile")}{(profile.Enabled ? "" : " (disabled)")}";
    }

    /// <summary>Formats one health-check list item using its name, type, and disabled state.</summary>
    /// <param name="sender">The health-check list.</param>
    /// <param name="e">The list formatting event data.</param>
    private void HealthCheckListFormat(object? sender, ListControlConvertEventArgs e)
    {
        if (e.ListItem is HealthCheckConfig healthCheck)
            e.Value = HealthCheckDisplay.ListItem(healthCheck);
    }

    private void HealthCheckListDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _healthCheckList.Items.Count ||
            _healthCheckList.Items[e.Index] is not HealthCheckConfig healthCheck)
        {
            e.DrawBackground();
            return;
        }

        ConfigurationIconListRenderer.DrawItem(
            e,
            _healthCheckList.Font,
            HealthCheckDisplay.ListItem(healthCheck),
            (graphics, bounds, color, _) =>
                ConfigurationItemIconRenderer.DrawHealthCheck(
                    graphics,
                    bounds,
                    healthCheck.Type,
                    color
                )
        );
    }

    /// <summary>Adds and selects a new supervisor profile.</summary>
    /// <param name="sender">The Add profile button.</param>
    /// <param name="e">The click event data.</param>
    private void AddProfileClicked(object? sender, EventArgs e)
    {
        var profile = new SupervisorProfileConfig
        {
            Name = CreateUniqueProfileName("New profile"),
            MonitorProcess = "",
            Applications = [],
            Services = [],
            Delays = [],
            HomeAssistantResources = [],
            MqttResources = [],
            ObsResources = [],
            StreamDeckResources = [],
            TwitchResources = [],
            AudioInterfaces = []
        };
        _profiles.Add(profile);
        BindProfileSelector(profile);
        _profileName.Focus();
    }

    /// <summary>Creates and selects a detached duplicate of the current supervisor profile.</summary>
    /// <param name="sender">The Duplicate button.</param>
    /// <param name="e">The click event data.</param>
    private void DuplicateProfileClicked(object? sender, EventArgs e)
    {
        if (SelectedProfile is not SupervisorProfileConfig selected)
            return;

        SupervisorProfileConfig duplicate = ConfigJson.Clone(selected);
        duplicate.ProfileId = Guid.NewGuid().ToString("N");
        duplicate.Name = CreateUniqueProfileName($"{DisplayName(selected.Name, "Profile")} copy");
        _profiles.Add(duplicate);
        BindProfileSelector(duplicate);
    }

    /// <summary>Removes the selected profile after explicit confirmation.</summary>
    /// <param name="sender">The Remove button.</param>
    /// <param name="e">The click event data.</param>
    private void RemoveProfileClicked(object? sender, EventArgs e)
    {
        if (SelectedProfile is not SupervisorProfileConfig selected ||
            !ConfirmRemoval("profile", DisplayName(selected.Name, "Unnamed profile")))
        {
            return;
        }

        _profiles.Remove(selected);
        BindProfileSelector();
    }

    /// <summary>Adds and selects a new helper application in the current profile.</summary>
    /// <param name="sender">The application Add button.</param>
    /// <param name="e">The click event data.</param>
    private void AddApplicationClicked(object? sender, EventArgs e)
    {
        if (SelectedProfile is not SupervisorProfileConfig profile)
            return;

        var application = new ManagedApplicationConfig();
        profile.Applications.Add(application);
        BindResourceList(profile, application);
        LoadSelectedResource();
        _applicationPath.Focus();
        UpdateStatus();
    }

    /// <summary>Removes the selected application after explicit confirmation.</summary>
    /// <param name="sender">The application Remove button.</param>
    /// <param name="e">The click event data.</param>
    private void RemoveApplicationClicked(object? sender, EventArgs e)
    {
        if (SelectedProfile is not SupervisorProfileConfig profile ||
            SelectedApplication is not ManagedApplicationConfig selected ||
            !ConfirmRemoval("application", SafeFileName(selected.Path, "New application")))
        {
            return;
        }

        profile.Applications.Remove(selected);
        ClearRemovedResourceDependencies(profile, selected.ResourceId);
        BindResourceList(profile);
        LoadSelectedResource();
        UpdateStatus();
    }

    /// <summary>Lets the user choose an application executable through the standard file picker.</summary>
    /// <param name="sender">The Browse button.</param>
    /// <param name="e">The click event data.</param>
    private void BrowseApplicationClicked(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose helper executable",
            Filter = "Applications (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (File.Exists(_applicationPath.Text))
            dialog.FileName = _applicationPath.Text;

        if (dialog.ShowDialog(this) == DialogResult.OK)
            _applicationPath.Text = dialog.FileName;
    }

    /// <summary>Adds a validated listener health check to the selected application.</summary>
    /// <param name="sender">The Add check button.</param>
    /// <param name="e">The click event data.</param>
    private void AddHealthCheckClicked(object? sender, EventArgs e)
    {
        if (SelectedApplication is not ManagedApplicationConfig application)
            return;

        var candidate = new HealthCheckConfig
        {
            Name = CreateUniqueHealthCheckName(application, "New listener"),
            Type = HealthCheckType.Listener,
            Protocol = ListenerProtocol.Tcp,
            Port = 12345
        };
        using var dialog = new HealthCheckEditorDialog(candidate);

        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null)
            return;

        application.HealthChecks.Add(dialog.Result);
        BindHealthCheckList(application, dialog.Result);
        UpdateStatus();
    }

    /// <summary>Edits the selected health check in a detached type-aware dialog.</summary>
    /// <param name="sender">The Edit button or health-check list.</param>
    /// <param name="e">The click or double-click event data.</param>
    private void EditHealthCheckClicked(object? sender, EventArgs e)
    {
        if (SelectedApplication is not ManagedApplicationConfig application ||
            SelectedHealthCheck is not HealthCheckConfig selected)
        {
            return;
        }

        int index = application.HealthChecks.IndexOf(selected);
        using var dialog = new HealthCheckEditorDialog(ConfigJson.Clone(selected));

        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null)
            return;

        application.HealthChecks[index] = dialog.Result;
        BindHealthCheckList(application, dialog.Result);
        UpdateStatus();
    }

    /// <summary>Removes the selected health check after explicit confirmation.</summary>
    /// <param name="sender">The health-check Remove button.</param>
    /// <param name="e">The click event data.</param>
    private void RemoveHealthCheckClicked(object? sender, EventArgs e)
    {
        if (SelectedApplication is not ManagedApplicationConfig application ||
            SelectedHealthCheck is not HealthCheckConfig selected ||
            !ConfirmRemoval("health check", DisplayName(selected.Name, "New health check")))
        {
            return;
        }

        application.HealthChecks.Remove(selected);
        BindHealthCheckList(application);
        UpdateStatus();
    }

    /// <summary>Adds and selects a new Windows service in the current profile.</summary>
    /// <param name="sender">The service Add button.</param>
    /// <param name="e">The click event data.</param>
    private void AddServiceClicked(object? sender, EventArgs e)
    {
        if (SelectedProfile is not SupervisorProfileConfig profile)
            return;

        InstalledServiceInfo? installedService = _installedServices.FirstOrDefault(candidate =>

            !profile.Services.Any(configured => string.Equals(

                configured.ServiceName,

                candidate.ServiceName,

                StringComparison.OrdinalIgnoreCase

            ))

        );



        if (installedService is null)

        {

            string message = _installedServices.Count == 0

                ? "No selectable third-party services were discovered. Use Refresh list after installing or starting the required service software."

                : "Every discovered third-party service is already configured in this profile.";
            MessageBox.Show(

                this,

                message,

                "No service available",

                MessageBoxButtons.OK,

                MessageBoxIcon.Information

            );

            return;

        }



        var service = new ManagedServiceConfig

        {

            ServiceName = installedService.ServiceName

        };

        WarnIfAutomaticService(installedService);

        profile.Services.Add(service);
        BindResourceList(profile, service);
        LoadSelectedResource();
        _serviceName.Focus();
        UpdateStatus();
    }

    /// <summary>Removes the selected service after explicit confirmation.</summary>
    /// <param name="sender">The service Remove button.</param>
    /// <param name="e">The click event data.</param>
    private void RemoveServiceClicked(object? sender, EventArgs e)
    {
        if (SelectedProfile is not SupervisorProfileConfig profile ||
            SelectedService is not ManagedServiceConfig selected ||
            !ConfirmRemoval("service", DisplayName(selected.ServiceName, "New service")))
        {
            return;
        }

        profile.Services.Remove(selected);
        ClearRemovedResourceDependencies(profile, selected.ResourceId);
        BindResourceList(profile);
        LoadSelectedResource();
        UpdateStatus();
    }

    /// <summary>Chooses the profile trigger from a searchable list of running processes.</summary>
    /// <param name="sender">The Pick running button.</param>
    /// <param name="e">The click event data.</param>
    private void PickMonitorProcessClicked(object? sender, EventArgs e)
    {
        using var picker = new RunningProcessPickerDialog();

        if (picker.ShowDialog(this) == DialogResult.OK)
            _monitorProcess.Text = picker.SelectedProcessName ?? "";
    }

    /// <summary>Validates the complete document and reports success without writing it.</summary>
    /// <param name="sender">The Validate button.</param>
    /// <param name="e">The click event data.</param>
    private void ValidateClicked(object? sender, EventArgs e)
    {
        try
        {
            ConfigFileWriter.Serialize(_configuration);
            MessageBox.Show(
                this,
                "Configuration is valid.",
                "AppSupervisor Configuration",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
        catch (Exception ex)
        {
            ShowValidationError(ex);
        }
    }

    /// <summary>Validates, checks for external file changes, atomically writes config.json, and closes for runtime reload.</summary>
    /// <param name="sender">The Save &amp; Apply button.</param>
    /// <param name="e">The click event data.</param>
    private void SaveClicked(object? sender, EventArgs e)
    {
        try
        {
            ConfigFileWriter.Serialize(_configuration);

            if (HasFileChangedExternally() && MessageBox.Show(
                this,
                "config.json changed on disk after this editor opened. Overwrite those external changes with the configuration shown here?",
                "Configuration changed externally",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2
            ) != DialogResult.Yes)
            {
                return;
            }

            ConfigFileWriter.SaveAtomic(_configPath, _configuration);
            _loadedWriteTimeUtc = GetWriteTimeUtc(_configPath);
            _hasUnsavedChanges = false;
            _closeWithoutUnsavedChangesPrompt = true;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            ShowValidationError(ex);
        }
    }

    /// <summary>Shows a complete validation or write failure without modifying the active file.</summary>
    /// <param name="exception">The failure to display.</param>
    private void ShowValidationError(Exception exception)
    {
        MessageBox.Show(
            this,
            exception.Message,
            "Configuration was not saved",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning
        );
    }

    /// <summary>Updates the footer with load warnings and current document counts.</summary>
    private void UpdateStatus()
    {
        UpdateDirtyState();
        if (_loadError is not null)
        {
            _statusLabel.ForeColor = Color.DarkRed;
            _statusLabel.Text = "The existing file could not be loaded. Saving will replace it with this new document.";
            _statusLabel.Tag = _loadError;
            return;
        }

        int applications = _profiles.Sum(profile => profile.Applications.Count);
        int services = _profiles.Sum(profile => profile.Services.Count);
        int healthChecks = _profiles.Sum(profile =>
            profile.Applications.Sum(application => application.HealthChecks.Count));
        _statusLabel.ForeColor = SystemColors.ControlText;
        _statusLabel.Text = $"{_profiles.Count} profile(s), {applications} application(s), {services} service(s), {healthChecks} health check(s)";
    }

    /// <summary>Serializes the editable model without requiring it to be currently valid.</summary>
    /// <returns>A stable JSON representation used only for change detection.</returns>
    private string SerializeEditingState()
    {
        return System.Text.Json.JsonSerializer.Serialize(
            _configuration,
            ConfigJson.CreateOptions()
        );
    }

    /// <summary>Compares the current editor model with its opening state and updates change actions.</summary>
    private void UpdateDirtyState()
    {
        _hasUnsavedChanges = _loadError is not null ||
            (_unchangedConfigurationState.Length > 0 &&
                !string.Equals(
                    SerializeEditingState(),
                    _unchangedConfigurationState,
                    StringComparison.Ordinal
                ));
        _validateButton.Enabled = _hasUnsavedChanges;
        _saveButton.Enabled = _hasUnsavedChanges;
    }

    /// <summary>Reads valid typed numeric text before WinForms commits it when focus changes.</summary>
    /// <param name="numeric">The numeric editor whose displayed value is required.</param>
    /// <returns>The displayed whole number, or its last committed value for incomplete text.</returns>
    private static int ReadDisplayedNumber(NumericUpDown numeric)
    {
        if (decimal.TryParse(
                numeric.Text,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.CurrentCulture,
                out decimal displayed) &&
            displayed >= numeric.Minimum &&
            displayed <= numeric.Maximum)
        {
            return Decimal.ToInt32(displayed);
        }

        return Decimal.ToInt32(numeric.Value);
    }

    /// <summary>Asks before closing an editor that contains unsaved configuration changes.</summary>
    /// <param name="e">The requested form-close operation.</param>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closeWithoutUnsavedChangesPrompt && _hasUnsavedChanges)
        {
            DialogResult discardResult = MessageBox.Show(
                this,
                "The configuration contains unsaved changes. Close and discard them?",
                "Discard unsaved changes?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2
            );

            if (discardResult != DialogResult.Yes)
            {
                DialogResult = DialogResult.None;
                e.Cancel = true;
                return;
            }

            _closeWithoutUnsavedChangesPrompt = true;
        }

        if (DelayCloseForHelperTest(e))
            return;

        base.OnFormClosing(e);
    }

    /// <summary>Closes the editor immediately during complete supervisor shutdown.</summary>
    internal void CloseWithoutUnsavedChangesPrompt()
    {
        _closeWithoutUnsavedChangesPrompt = true;
        Close();
    }

    /// <summary>Returns whether the destination's write timestamp differs from the file loaded by this editor.</summary>
    /// <returns><see langword="true"/> when another process changed, created, or removed config.json.</returns>
    private bool HasFileChangedExternally()
    {
        return GetWriteTimeUtc(_configPath) != _loadedWriteTimeUtc;
    }

    /// <summary>Creates a case-insensitively unique supervisor-profile name.</summary>
    /// <param name="baseName">The preferred base name.</param>
    /// <returns>A name not currently used by another profile.</returns>
    private string CreateUniqueProfileName(string baseName)
    {
        var existing = _profiles
            .Select(profile => profile.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!existing.Contains(baseName))
            return baseName;

        for (int suffix = 2; ; suffix++)
        {
            string candidate = $"{baseName} {suffix}";

            if (!existing.Contains(candidate))
                return candidate;
        }
    }

    /// <summary>Creates a case-insensitively unique health-check name within one application.</summary>
    /// <param name="application">The owning helper application.</param>
    /// <param name="baseName">The preferred base name.</param>
    /// <returns>A name not currently used by another check in the helper.</returns>
    private static string CreateUniqueHealthCheckName(
        ManagedApplicationConfig application,
        string baseName)
    {
        var existing = application.HealthChecks
            .Select(healthCheck => healthCheck.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!existing.Contains(baseName))
            return baseName;

        for (int suffix = 2; ; suffix++)
        {
            string candidate = $"{baseName} {suffix}";

            if (!existing.Contains(candidate))
                return candidate;
        }
    }

    /// <summary>Asks for confirmation before removing one configuration object.</summary>
    /// <param name="kind">The object kind displayed in the question.</param>
    /// <param name="name">The object name displayed in the question.</param>
    /// <returns><see langword="true"/> only when the user confirms removal.</returns>
    private bool ConfirmRemoval(string kind, string name)
    {
        return MessageBox.Show(
            this,
            $"Remove {kind} '{name}' from this configuration?",
            "Confirm removal",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2
        ) == DialogResult.Yes;
    }

    /// <summary>Loads the current file for editing, returning an empty document with a warning when it is invalid.</summary>
    /// <param name="path">The configuration path.</param>
    /// <returns>The editable profiles and optional load error.</returns>
    private static (AppSupervisorConfig Configuration, string? Error)
        LoadConfigurationForEditing(string path)
    {
        try
        {
            return (ConfigLoader.Load(path), null);
        }
        catch (Exception ex)
        {
            return (new AppSupervisorConfig(), ex.Message);
        }
    }

    /// <summary>Gets a file's UTC write timestamp, or null when the file does not exist.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The UTC write timestamp or null.</returns>
    private static DateTime? GetWriteTimeUtc(string path)
    {
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    }

    /// <summary>Returns a trimmed display name or a fallback for empty values.</summary>
    /// <param name="value">The configured name.</param>
    /// <param name="fallback">The fallback label.</param>
    /// <returns>The displayable name.</returns>
    private static string DisplayName(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    /// <summary>Returns an executable filename without throwing for malformed or empty paths.</summary>
    /// <param name="path">The configured path.</param>
    /// <param name="fallback">The fallback label.</param>
    /// <returns>The executable filename or fallback.</returns>
    private static string SafeFileName(string path, string fallback)
    {
        try
        {
            string fileName = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(fileName) ? fallback : fileName;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>Creates a consistently sized command button and attaches its click handler.</summary>
    /// <param name="text">The button label.</param>
    /// <param name="clickHandler">The click handler.</param>
    /// <returns>The configured button.</returns>
    private static Button CreateButton(string text, EventHandler clickHandler)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(72, 27),
            Margin = new Padding(6, 0, 0, 0),
            Padding = new Padding(8, 0, 8, 0)
        };
        button.Click += clickHandler;
        return button;
    }

    /// <summary>Creates a standard fixed-list/expanding-editor split container.</summary>
    /// <returns>The configured split container.</returns>
    private static SplitContainer CreateListEditorSplit()
    {
        return new SplitContainer
        {
            Dock = DockStyle.Fill,
            Size = new Size(900, 600),
            FixedPanel = FixedPanel.Panel1,
            SplitterDistance = 280,
            Panel1MinSize = 220,
            Panel2MinSize = 500
        };
    }

    /// <summary>Creates a standard two-column editor table.</summary>
    /// <returns>The configured table.</returns>
    private static TableLayoutPanel CreateEditorTable()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return layout;
    }

    /// <summary>Adds one labeled row to a standard editor table.</summary>
    /// <param name="layout">The target editor table.</param>
    /// <param name="labelText">The row label.</param>
    /// <param name="control">The row editor or explanatory control.</param>
    private static Label AddEditorRow(
        TableLayoutPanel layout,
        string labelText,
        Control control,
        bool alignTop = false)
    {
        int row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label
        {
            Text = labelText,
            AutoSize = true,
            Anchor = alignTop
                ? AnchorStyles.Top | AnchorStyles.Left
                : AnchorStyles.Left,
            Margin = new Padding(0, 8, 12, 6)
        };
        layout.Controls.Add(label, 0, row);
        control.Margin = new Padding(0, 4, 0, 7);
        layout.Controls.Add(control, 1, row);
        return label;
    }

    /// <summary>Gets the currently selected supervisor profile.</summary>
    private SupervisorProfileConfig? SelectedProfile =>
        _profileSelector.SelectedItem as SupervisorProfileConfig;

    /// <summary>Gets the currently selected helper application or service.</summary>
    private ManagedResourceConfig? SelectedResource =>
        _resourceList.SelectedItem as ManagedResourceConfig;

    /// <summary>Gets the currently selected helper application.</summary>
    private ManagedApplicationConfig? SelectedApplication =>
        SelectedResource as ManagedApplicationConfig;

    /// <summary>Gets the currently selected service.</summary>
    private ManagedServiceConfig? SelectedService =>
        SelectedResource as ManagedServiceConfig;

    /// <summary>Gets the currently selected explicit startup delay.</summary>
    private DelayResourceConfig? SelectedDelay =>
        SelectedResource as DelayResourceConfig;

    /// <summary>Gets the currently selected Home Assistant action.</summary>
    private HomeAssistantResourceConfig? SelectedHomeAssistant =>
        SelectedResource as HomeAssistantResourceConfig;

    /// <summary>Gets the currently selected MQTT publish.</summary>
    private MqttResourceConfig? SelectedMqtt =>
        SelectedResource as MqttResourceConfig;

    /// <summary>Gets the currently selected OBS action.</summary>
    private ObsResourceConfig? SelectedObs =>
        SelectedResource as ObsResourceConfig;

    /// <summary>Gets the currently selected Stream Deck action.</summary>
    private StreamDeckResourceConfig? SelectedStreamDeck =>
        SelectedResource as StreamDeckResourceConfig;

    /// <summary>Gets the currently selected Twitch action.</summary>
    private TwitchResourceConfig? SelectedTwitch =>
        SelectedResource as TwitchResourceConfig;

    /// <summary>Gets the currently selected Windows audio endpoint action.</summary>
    private AudioInterfaceResourceConfig? SelectedAudioInterface =>
        SelectedResource as AudioInterfaceResourceConfig;

    /// <summary>Gets the currently selected health check.</summary>
    private HealthCheckConfig? SelectedHealthCheck =>
        _healthCheckList.SelectedItem as HealthCheckConfig;
}
