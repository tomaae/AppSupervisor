using System.ComponentModel;
using AppSupervisor.HomeAssistant;
using AppSupervisor.Obs;
using AppSupervisor.SteamVr;
using AppSupervisor.SupervisorApi;
using AppSupervisor.Twitch;

namespace AppSupervisor.ConfigurationUI;

/// <summary>Provides global integration configuration independently of the selected profile.</summary>
public sealed partial class ConfigurationEditorForm
{
    private readonly CheckBox _supervisorApiEnabled = new()
    {
        Text = "Enable read-only WS API",
        AutoSize = true
    };
    private readonly Func<
        HomeAssistantIntegrationConfig,
        CancellationToken,
        Task<HomeAssistantCatalog>> _homeAssistantCatalogLoader;
    private readonly TextBox _homeAssistantUrl = new() { Dock = DockStyle.Fill };
    private readonly TextBox _homeAssistantToken = new()
    {
        Dock = DockStyle.Fill,
        UseSystemPasswordChar = true
    };
    private Button _testHomeAssistantButton = null!;
    private HomeAssistantCatalog? _homeAssistantCatalog;
    private string _homeAssistantCatalogConnectionKey = "";
    private readonly Func<
        ObsIntegrationConfig,
        CancellationToken,
        Task<ObsCatalog>> _obsCatalogLoader;
    private readonly TextBox _obsHost = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _obsPort = new()
    {
        Minimum = 1,
        Maximum = 65_535,
        Width = 110
    };
    private readonly TextBox _obsPassword = new()
    {
        Dock = DockStyle.Fill,
        UseSystemPasswordChar = true
    };
    private Button _testObsButton = null!;
    private ObsCatalog? _obsCatalog;
    private string _obsCatalogConnectionKey = "";
    private readonly Label _twitchConnectionStatus = new()
    {
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Text = "Not connected"
    };
    private Button _connectTwitchButton = null!;
    private Button _disconnectTwitchButton = null!;
    private bool _twitchAuthorizationPending;
    private readonly Func<CancellationToken, Task<SteamVrSnapshot>> _steamVrDeviceLoader;
    private readonly ComboBox _logLevel = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        FormattingEnabled = true,
        Width = 180
    };
    private readonly CheckBox _steamVrEnabled = new()
    {
        Text = "Monitor expected SteamVR devices",
        AutoSize = true
    };
    private readonly NumericUpDown _steamVrReminderMinutes = new()
    {
        Minimum = 1,
        Maximum = 1_440,
        Width = 100
    };
    private readonly NotificationTargetsControl _steamVrNotifications = new();
    private readonly BindingList<SteamVrDeviceEditorRow> _steamVrRows = [];
    private readonly DataGridView _steamVrDevices = new()
    {
        Dock = DockStyle.Fill,
        AutoGenerateColumns = false,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        MultiSelect = true,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        RowHeadersVisible = false
    };
    private Button _discoverSteamVrDevicesButton = null!;

    private TabPage BuildIntegrationsPage()
    {
        var page = new TabPage("Integrations");
        var scrolling = new Panel
        {
            Name = "IntegrationsScrollPanel",
            Dock = DockStyle.Fill,
            AutoScroll = true
        };
        var integrationsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 8
        };
        integrationsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        integrationsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        integrationsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        integrationsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        integrationsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        integrationsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        integrationsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        integrationsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        integrationsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        integrationsLayout.Controls.Add(BuildSupervisorApiIntegrationGroup(), 0, 0);
        integrationsLayout.Controls.Add(BuildHomeAssistantIntegrationGroup(), 0, 1);
        integrationsLayout.Controls.Add(BuildMqttIntegrationGroup(), 0, 2);
        integrationsLayout.Controls.Add(BuildObsIntegrationGroup(), 0, 3);
        integrationsLayout.Controls.Add(BuildTwitchIntegrationGroup(), 0, 4);
        integrationsLayout.Controls.Add(BuildBluetoothIntegrationGroup(), 0, 5);
        var group = new GroupBox
        {
            Text = "Global — SteamVR device monitoring",
            Dock = DockStyle.Top,
            MinimumSize = new Size(0, 480),
            Padding = new Padding(12),
            Margin = new Padding(0, 10, 0, 0)
        };
        TableLayoutPanel settings = CreateEditorTable();
        settings.Padding = new Padding(0, 4, 0, 8);
        AddSpanningEditorRow(settings, _steamVrEnabled);
        AddEditorRow(settings, "Timing", new Label
        {
            Text = "30-second startup grace, checks every 30 seconds, offline after 2 failed checks; " +
                "FBT trackers arm when first connected in the session",
            AutoSize = true,
            Anchor = AnchorStyles.Left
        });
        AddEditorRow(settings, "Reminder interval", BuildReminderEditor());
        AddEditorRow(
            settings,
            "Notifications",
            BuildNotificationTestPanel(_steamVrNotifications, TestSteamVrNotificationClicked)
        );

        ConfigureSteamVrGrid();
        var devicePanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        var deviceButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 6, 0, 0)
        };
        _discoverSteamVrDevicesButton = CreateButton(
            "Discover from running SteamVR",
            DiscoverSteamVrDevicesClicked
        );
        Button removeButton = CreateButton("Remove selected", RemoveSteamVrDevicesClicked);
        deviceButtons.Controls.Add(_discoverSteamVrDevicesButton);
        deviceButtons.Controls.Add(removeButton);
        devicePanel.Controls.Add(_steamVrDevices);
        devicePanel.Controls.Add(deviceButtons);

        group.Controls.Add(devicePanel);
        group.Controls.Add(settings);
        integrationsLayout.Controls.Add(group, 0, 6);
        integrationsLayout.Controls.Add(BuildLoggingIntegrationGroup(), 0, 7);
        scrolling.Controls.Add(integrationsLayout);
        page.Controls.Add(scrolling);

        LoadSupervisorApiIntegration();
        LoadHomeAssistantIntegration();
        LoadMqttIntegration();
        LoadObsIntegration();
        LoadTwitchIntegration();
        LoadBluetoothIntegration();
        LoadSteamVrIntegration();
        LoadLoggingIntegration();
        _steamVrEnabled.CheckedChanged += SteamVrSettingsChanged;
        _steamVrReminderMinutes.ValueChanged += SteamVrSettingsChanged;
        _steamVrReminderMinutes.TextChanged += SteamVrSettingsChanged;
        _steamVrNotifications.TargetsChanged += SteamVrSettingsChanged;
        _steamVrDevices.CellValueChanged += SteamVrDeviceCellValueChanged;
        _steamVrDevices.CurrentCellDirtyStateChanged += SteamVrDeviceCellDirtyStateChanged;
        _bluetoothScanInterval.ValueChanged += BluetoothSettingsChanged;
        _bluetoothScanInterval.TextChanged += BluetoothSettingsChanged;
        _bluetoothPresenceTimeout.ValueChanged += BluetoothSettingsChanged;
        _bluetoothPresenceTimeout.TextChanged += BluetoothSettingsChanged;
        _bluetoothDevices.CellValueChanged += BluetoothDeviceCellValueChanged;
        return page;
    }

    /// <summary>Builds the global diagnostic log severity selector at the bottom of Integrations.</summary>
    private Control BuildLoggingIntegrationGroup()
    {
        var group = new GroupBox
        {
            Text = "Global — Diagnostic logging",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = new Padding(0, 10, 0, 0)
        };
        TableLayoutPanel layout = CreateEditorTable();
        _logLevel.Items.AddRange(
            Enum.GetValues<SupervisorLogLevel>().Cast<object>().ToArray()
        );
        _logLevel.Format += (_, args) =>
        {
            if (args.ListItem is SupervisorLogLevel level)
                args.Value = level.ToString();
        };
        AddEditorRow(layout, "Log level", _logLevel);
        AddEditorRow(layout, "", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(720, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Info is the default. Trace includes detailed diagnostic flow; Warning and Error reduce routine log output."
        });
        group.Controls.Add(layout);
        return group;
    }

    /// <summary>Loads and observes the configured diagnostic log severity.</summary>
    private void LoadLoggingIntegration()
    {
        _logLevel.SelectedItem = _configuration.Integrations.LogLevel;
        _logLevel.SelectedValueChanged += LogLevelChanged;
    }

    /// <summary>Updates the editable log severity when its selection changes.</summary>
    private void LogLevelChanged(object? sender, EventArgs e)
    {
        if (_loadingControls || _logLevel.SelectedItem is not SupervisorLogLevel level)
            return;

        _configuration.Integrations.LogLevel = level;
        UpdateStatus();
    }

    /// <summary>Builds the global loopback-only Supervisor API toggle.</summary>
    private Control BuildSupervisorApiIntegrationGroup()
    {
        var group = new GroupBox
        {
            Text = "Global — Supervisor API",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = Padding.Empty
        };
        TableLayoutPanel layout = CreateEditorTable();
        AddSpanningEditorRow(layout, _supervisorApiEnabled);
        AddEditorRow(layout, "Endpoint", new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Text = SupervisorApiServer.BaseAddress
        });
        AddEditorRow(layout, "Access", new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = SystemColors.GrayText,
            Text = "Passwordless, read-only JSON; accepts connections from this computer only."
        });
        group.Controls.Add(layout);
        return group;
    }

    private void LoadSupervisorApiIntegration()
    {
        _supervisorApiEnabled.Checked = _configuration.Integrations.SupervisorApi.Enabled;
        _supervisorApiEnabled.CheckedChanged += SupervisorApiSettingsChanged;
    }

    private void SupervisorApiSettingsChanged(object? sender, EventArgs e)
    {
        if (_loadingControls)
            return;

        _configuration.Integrations.SupervisorApi.Enabled = _supervisorApiEnabled.Checked;
        UpdateStatus();
    }

    private Control BuildTwitchIntegrationGroup()
    {
        var group = new GroupBox
        {
            Text = "Global — Twitch broadcaster",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = new Padding(0, 10, 0, 0)
        };
        TableLayoutPanel layout = CreateEditorTable();
        AddEditorRow(layout, "Connection", _twitchConnectionStatus);
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty
        };
        _connectTwitchButton = CreateButton("Connect Twitch", ConnectTwitchClicked);
        _disconnectTwitchButton = CreateButton("Disconnect", DisconnectTwitchClicked);
        buttons.Controls.Add(_connectTwitchButton);
        buttons.Controls.Add(_disconnectTwitchButton);
        AddEditorRow(layout, "", buttons);
        AddEditorRow(layout, "", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(720, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Authorization opens Twitch in your browser once. AppSupervisor renews it automatically while running; rotating OAuth credentials are encrypted for this Windows user."
        });
        group.Controls.Add(layout);
        return group;
    }

    private void LoadTwitchIntegration()
    {
        _ = RefreshTwitchConnectionStatusAsync();
    }

    private async Task RefreshTwitchConnectionStatusAsync()
    {
        try
        {
            using var authorization = new TwitchAuthorizationService(
                new TwitchIntegrationConfig()
            );
            TwitchAuthorizationStatus status = await authorization.GetStatusAsync(CancellationToken.None);
            if (IsDisposed)
                return;
            ApplyTwitchConnectionStatus(status);
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                _twitchConnectionStatus.Text = ex.Message;
                _connectTwitchButton.Enabled = true;
                _disconnectTwitchButton.Enabled = true;
            }
        }
    }

    private async void ConnectTwitchClicked(object? sender, EventArgs e)
    {
        if (_twitchAuthorizationPending)
            return;
        _twitchAuthorizationPending = true;
        _connectTwitchButton.Enabled = false;
        bool connected = false;
        try
        {
            TwitchAuthorizationStatus status = await TwitchConnectionFlow.ConnectAsync(
                status => _twitchConnectionStatus.Text = status,
                CancellationToken.None
            );
            ApplyTwitchConnectionStatus(status);
            connected = true;
        }
        catch (Exception ex)
        {
            _twitchConnectionStatus.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "Twitch connection failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _twitchAuthorizationPending = false;
            if (!_connectTwitchButton.IsDisposed)
                _connectTwitchButton.Enabled = !connected;
        }
    }

    private async void DisconnectTwitchClicked(object? sender, EventArgs e)
    {
        if (MessageBox.Show(this, "Disconnect the stored Twitch broadcaster authorization?", "Disconnect Twitch", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _disconnectTwitchButton.Enabled = false;
        try
        {
            using var authorization = new TwitchAuthorizationService(
                new TwitchIntegrationConfig()
            );
            await authorization.DisconnectAsync(CancellationToken.None);
            ApplyTwitchConnectionStatus(TwitchAuthorizationStatus.Disconnected);
        }
        catch (Exception ex)
        {
            _twitchConnectionStatus.Text = ex.Message;
            _connectTwitchButton.Enabled = true;
            _disconnectTwitchButton.Enabled = true;
            MessageBox.Show(this, ex.Message, "Twitch disconnect failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Updates Twitch connection text and mutually exclusive connect controls.</summary>
    /// <param name="status">The current persistent broadcaster authorization state.</param>
    private void ApplyTwitchConnectionStatus(TwitchAuthorizationStatus status)
    {
        _twitchConnectionStatus.Text = status.Connected
            ? $"Connected as {status.Login}"
            : "Not connected";
        _connectTwitchButton.Enabled = !status.Connected && !_twitchAuthorizationPending;
        _disconnectTwitchButton.Enabled = status.Connected;
    }

    /// <summary>Builds global OBS WebSocket endpoint and password settings.</summary>
    private Control BuildObsIntegrationGroup()
    {
        var group = new GroupBox
        {
            Text = "Global — OBS WebSocket",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = new Padding(0, 10, 0, 0)
        };
        TableLayoutPanel layout = CreateEditorTable();
        AddEditorRow(layout, "Host", _obsHost);
        AddEditorRow(layout, "Port", _obsPort);
        AddEditorRow(layout, "Password", _obsPassword);
        _testObsButton = CreateButton("Test connection", TestObsConnectionClicked);
        AddEditorRow(layout, "", _testObsButton);
        AddEditorRow(layout, "", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(720, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Uses the standard OBS WebSocket 5.x protocol over ws. The password is masked here but stored in the local configuration file."
        });
        group.Controls.Add(layout);
        return group;
    }

    private void LoadObsIntegration()
    {
        ObsIntegrationConfig configuration = _configuration.Integrations.Obs;
        _obsHost.Text = configuration.Host;
        _obsPort.Value = Math.Clamp(configuration.Port, 1, 65_535);
        _obsPassword.Text = configuration.Password;
        _obsHost.TextChanged += ObsSettingsChanged;
        _obsPort.ValueChanged += ObsSettingsChanged;
        _obsPort.TextChanged += ObsSettingsChanged;
        _obsPassword.TextChanged += ObsSettingsChanged;
    }

    private void ObsSettingsChanged(object? sender, EventArgs e)
    {
        if (_loadingControls)
            return;

        ObsIntegrationConfig configuration = _configuration.Integrations.Obs;
        configuration.Host = _obsHost.Text;
        configuration.Port = ReadDisplayedNumber(_obsPort);
        configuration.Password = _obsPassword.Text;
        _obsCatalog = null;
        _obsCatalogConnectionKey = "";
        bool wasLoading = _loadingControls;
        _loadingControls = true;

        try
        {
            ClearObsSelectors();
        }
        finally
        {
            _loadingControls = wasLoading;
        }
        UpdateStatus();
    }

    private async void TestObsConnectionClicked(object? sender, EventArgs e)
    {
        _testObsButton.Enabled = false;

        try
        {
            ObsCatalog catalog = await LoadObsCatalogAsync(
                forceRefresh: true,
                CancellationToken.None
            );
            MessageBox.Show(
                this,
                $"Connected to OBS WebSocket {catalog.Version}. Found " +
                $"{catalog.Scenes.Count} scenes and {catalog.AudioInputs.Count} audio sources.",
                "OBS connection succeeded",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "OBS connection failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }
        finally
        {
            if (!_testObsButton.IsDisposed)
                _testObsButton.Enabled = true;
        }
    }

    private async Task<ObsCatalog> LoadObsCatalogAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        ObsIntegrationConfig configuration = _configuration.Integrations.Obs;
        string key = $"{configuration.Host.Trim()}\n{configuration.Port}\n{configuration.Password}";

        if (!forceRefresh && _obsCatalog is not null &&
            string.Equals(key, _obsCatalogConnectionKey, StringComparison.Ordinal))
        {
            return _obsCatalog;
        }

        if (string.IsNullOrWhiteSpace(configuration.Host) ||
            configuration.Port is < 1 or > 65_535)
        {
            throw new InvalidOperationException(
                "Enter a valid global OBS WebSocket host and port first."
            );
        }

        ObsCatalog catalog = await _obsCatalogLoader(
            new ObsIntegrationConfig
            {
                Host = configuration.Host.Trim(),
                Port = configuration.Port,
                Password = configuration.Password
            },
            cancellationToken
        );
        _obsCatalog = catalog;
        _obsCatalogConnectionKey = key;
        return catalog;
    }

    /// <summary>Builds global Home Assistant authentication settings and the connection test.</summary>
    private Control BuildHomeAssistantIntegrationGroup()
    {
        var group = new GroupBox
        {
            Text = "Global — Home Assistant authentication",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = Padding.Empty
        };
        TableLayoutPanel layout = CreateEditorTable();
        AddEditorRow(layout, "URL", _homeAssistantUrl);
        AddEditorRow(layout, "Long-lived token", _homeAssistantToken);
        _testHomeAssistantButton = CreateButton(
            "Test connection",
            TestHomeAssistantConnectionClicked
        );
        AddEditorRow(layout, "", _testHomeAssistantButton);
        AddEditorRow(layout, "", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(720, 0),
            ForeColor = SystemColors.GrayText,
            Text = "These credentials are shared by every Home Assistant resource. The token is masked in the editor but stored in the local configuration file."
        });
        group.Controls.Add(layout);
        return group;
    }

    private void LoadHomeAssistantIntegration()
    {
        HomeAssistantIntegrationConfig configuration =
            _configuration.Integrations.HomeAssistant;
        _homeAssistantUrl.Text = configuration.Url;
        _homeAssistantToken.Text = configuration.Token;
        _homeAssistantUrl.TextChanged += HomeAssistantSettingsChanged;
        _homeAssistantToken.TextChanged += HomeAssistantSettingsChanged;
    }

    private void HomeAssistantSettingsChanged(object? sender, EventArgs e)
    {
        if (_loadingControls)
            return;

        HomeAssistantIntegrationConfig configuration =
            _configuration.Integrations.HomeAssistant;
        configuration.Url = _homeAssistantUrl.Text;
        configuration.Token = _homeAssistantToken.Text;
        _homeAssistantCatalog = null;
        _homeAssistantCatalogConnectionKey = "";
        ClearHomeAssistantSelectors();
        UpdateStatus();
    }

    private async void TestHomeAssistantConnectionClicked(object? sender, EventArgs e)
    {
        _testHomeAssistantButton.Enabled = false;

        try
        {
            HomeAssistantCatalog catalog = await LoadHomeAssistantCatalogAsync(
                forceRefresh: true,
                CancellationToken.None
            );
            MessageBox.Show(
                this,
                $"Connected to Home Assistant {catalog.Version}. Found " +
                $"{catalog.Services.Count} supported services and {catalog.Entities.Count} entities.",
                "Home Assistant connection succeeded",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Home Assistant connection failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }
        finally
        {
            if (!_testHomeAssistantButton.IsDisposed)
                _testHomeAssistantButton.Enabled = true;
        }
    }

    private async Task<HomeAssistantCatalog> LoadHomeAssistantCatalogAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        HomeAssistantIntegrationConfig configuration =
            _configuration.Integrations.HomeAssistant;
        string key = $"{configuration.Url.Trim()}\n{configuration.Token.Trim()}";

        if (!forceRefresh && _homeAssistantCatalog is not null &&
            string.Equals(key, _homeAssistantCatalogConnectionKey, StringComparison.Ordinal))
        {
            return _homeAssistantCatalog;
        }

        if (string.IsNullOrWhiteSpace(configuration.Url) ||
            string.IsNullOrWhiteSpace(configuration.Token))
        {
            throw new InvalidOperationException(
                "Enter the global Home Assistant URL and long-lived token first."
            );
        }

        HomeAssistantCatalog catalog = await _homeAssistantCatalogLoader(
            new HomeAssistantIntegrationConfig
            {
                Url = configuration.Url.Trim(),
                Token = configuration.Token.Trim()
            },
            cancellationToken
        );
        _homeAssistantCatalog = catalog;
        _homeAssistantCatalogConnectionKey = key;
        return catalog;
    }

    /// <summary>Adds a full-width integration toggle starting at the settings label column.</summary>
    /// <param name="layout">The target settings table.</param>
    /// <param name="control">The section-level control to span across both columns.</param>
    private static void AddSpanningEditorRow(TableLayoutPanel layout, Control control)
    {
        int row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Margin = new Padding(0, 4, 0, 7);
        layout.Controls.Add(control, 0, row);
        layout.SetColumnSpan(control, 2);
    }

    private Control BuildReminderEditor()
    {
        var panel = new Panel
        {
            AutoSize = false,
            Size = new Size(180, _steamVrReminderMinutes.PreferredHeight),
            Padding = Padding.Empty,
            Margin = Padding.Empty
        };
        _steamVrReminderMinutes.Margin = Padding.Empty;
        _steamVrReminderMinutes.Location = Point.Empty;
        var suffix = new Label
        {
            Text = "minutes",
            AutoSize = true,
            Location = new Point(_steamVrReminderMinutes.Right + 4, 5)
        };
        panel.Controls.Add(_steamVrReminderMinutes);
        panel.Controls.Add(suffix);
        return panel;
    }

    private void ConfigureSteamVrGrid()
    {
        _steamVrDevices.Columns.Add(new DataGridViewCheckBoxColumn
        {
            DataPropertyName = nameof(SteamVrDeviceEditorRow.Enabled),
            HeaderText = "Monitor",
            Width = 65
        });
        _steamVrDevices.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(SteamVrDeviceEditorRow.Name),
            HeaderText = "Name",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 120
        });
        _steamVrDevices.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(SteamVrDeviceEditorRow.ClassText),
            HeaderText = "Type",
            ReadOnly = true,
            Width = 110
        });
        _steamVrDevices.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(SteamVrDeviceEditorRow.RoleText),
            HeaderText = "Assignment",
            ReadOnly = true,
            Width = 105
        });
        _steamVrDevices.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(SteamVrDeviceEditorRow.SerialNumber),
            HeaderText = "Serial number",
            ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 100
        });
        _steamVrDevices.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(SteamVrDeviceEditorRow.ModelNumber),
            HeaderText = "Model",
            ReadOnly = true,
            Width = 130
        });
        _steamVrDevices.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(SteamVrDeviceEditorRow.Status),
            HeaderText = "Last discovery",
            ReadOnly = true,
            Width = 110
        });
        _steamVrDevices.DataSource = _steamVrRows;
    }

    private void LoadSteamVrIntegration()
    {
        SteamVrIntegrationConfig configuration = _configuration.Integrations.SteamVr;
        _steamVrEnabled.Checked = configuration.Enabled;
        _steamVrReminderMinutes.Value = Math.Clamp(
            configuration.ReminderIntervalMinutes,
            Decimal.ToInt32(_steamVrReminderMinutes.Minimum),
            Decimal.ToInt32(_steamVrReminderMinutes.Maximum)
        );
        _steamVrNotifications.LoadTargets(configuration.Notifications.Target);

        foreach (SteamVrDeviceConfig device in configuration.Devices)
            _steamVrRows.Add(SteamVrDeviceEditorRow.FromConfig(device));
    }

    private void SteamVrSettingsChanged(object? sender, EventArgs e)
    {
        if (_loadingControls)
            return;

        SaveSteamVrIntegrationFromControls();
        UpdateStatus();
    }

    private void SteamVrDeviceCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0)
            return;

        SaveSteamVrIntegrationFromControls();
        UpdateStatus();
    }

    private void SteamVrDeviceCellDirtyStateChanged(object? sender, EventArgs e)
    {
        if (_steamVrDevices.IsCurrentCellDirty)
            _steamVrDevices.CommitEdit(DataGridViewDataErrorContexts.Commit);
    }

    private void SaveSteamVrIntegrationFromControls()
    {
        SteamVrIntegrationConfig configuration = _configuration.Integrations.SteamVr;
        configuration.Enabled = _steamVrEnabled.Checked;
        configuration.ReminderIntervalMinutes = ReadDisplayedNumber(_steamVrReminderMinutes);
        configuration.Notifications.Target = [.. _steamVrNotifications.SelectedTargets];
        configuration.Devices = _steamVrRows.Select(row => row.ToConfig()).ToList();
    }

    private async void DiscoverSteamVrDevicesClicked(object? sender, EventArgs e)
    {
        _discoverSteamVrDevicesButton.Enabled = false;

        try
        {
            SteamVrSnapshot snapshot = await _steamVrDeviceLoader(CancellationToken.None);

            if (!snapshot.SteamVrActive)
            {
                MessageBox.Show(
                    this,
                    "SteamVR is not currently running. AppSupervisor will never start it for discovery.",
                    "SteamVR is inactive",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
                return;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.Error))
                throw new InvalidOperationException(snapshot.Error);

            var observed = snapshot.Devices.ToDictionary(
                device => device.SerialNumber,
                StringComparer.OrdinalIgnoreCase
            );

            foreach (SteamVrDeviceEditorRow row in _steamVrRows)
            {
                row.Status = observed.TryGetValue(row.SerialNumber, out SteamVrDeviceSnapshot? device)
                    ? device.Connected ? "Connected" : "Offline"
                    : "Not detected";
            }

            foreach (SteamVrDeviceSnapshot device in snapshot.Devices)
            {
                SteamVrDeviceEditorRow? existing = _steamVrRows.FirstOrDefault(row =>
                    string.Equals(row.SerialNumber, device.SerialNumber, StringComparison.OrdinalIgnoreCase));

                if (existing is not null)
                {
                    existing.ModelNumber = device.ModelNumber;
                    existing.DeviceClass = device.DeviceClass;
                    existing.Role = device.Role;
                    continue;
                }

                _steamVrRows.Add(SteamVrDeviceEditorRow.FromSnapshot(device));
            }

            _steamVrDevices.Refresh();
            SaveSteamVrIntegrationFromControls();
            UpdateStatus();

            if (snapshot.Devices.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "SteamVR is running, but it did not expose any controllers, trackers, or tracking references.",
                    "No supported devices found",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "SteamVR discovery failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }
        finally
        {
            if (!_discoverSteamVrDevicesButton.IsDisposed)
                _discoverSteamVrDevicesButton.Enabled = true;
        }
    }

    private void RemoveSteamVrDevicesClicked(object? sender, EventArgs e)
    {
        SteamVrDeviceEditorRow[] selected = _steamVrDevices.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(row => row.DataBoundItem)
            .OfType<SteamVrDeviceEditorRow>()
            .ToArray();

        foreach (SteamVrDeviceEditorRow row in selected)
            _steamVrRows.Remove(row);

        if (selected.Length > 0)
        {
            SaveSteamVrIntegrationFromControls();
            UpdateStatus();
        }
    }

    private void TestSteamVrNotificationClicked(object? sender, EventArgs e)
        => PublishTestNotification(_steamVrNotifications.SelectedTargets, "SteamVR integration");

    private static Task<SteamVrSnapshot> LoadSteamVrDevicesAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            using var source = new OpenVrDeviceSource();
            return source.Capture();
        }, cancellationToken);
    }

    private sealed class SteamVrDeviceEditorRow
    {
        public bool Enabled { get; set; }
        public string Name { get; set; } = "";
        public string SerialNumber { get; set; } = "";
        public string ModelNumber { get; set; } = "";
        public SteamVrDeviceClass DeviceClass { get; set; }
        public SteamVrDeviceRole Role { get; set; }
        public string Status { get; set; } = "Not checked";
        public string ClassText => SteamVrDeviceDisplay.ClassName(DeviceClass);
        public string RoleText => DeviceClass == SteamVrDeviceClass.TrackingReference
            ? "—"
            : SteamVrDeviceDisplay.RoleName(Role);

        public static SteamVrDeviceEditorRow FromConfig(SteamVrDeviceConfig device) => new()
        {
            Enabled = device.Enabled,
            Name = device.Name,
            SerialNumber = device.SerialNumber,
            ModelNumber = device.ModelNumber,
            DeviceClass = device.DeviceClass,
            Role = device.Role
        };

        public static SteamVrDeviceEditorRow FromSnapshot(SteamVrDeviceSnapshot device) => new()
        {
            Enabled = true,
            Name = string.IsNullOrWhiteSpace(device.ModelNumber)
                ? $"{SteamVrDeviceDisplay.ClassName(device.DeviceClass)} {device.SerialNumber}"
                : device.ModelNumber,
            SerialNumber = device.SerialNumber,
            ModelNumber = device.ModelNumber,
            DeviceClass = device.DeviceClass,
            Role = device.Role,
            Status = device.Connected ? "Connected" : "Offline"
        };

        public SteamVrDeviceConfig ToConfig() => new()
        {
            Enabled = Enabled,
            Name = Name,
            SerialNumber = SerialNumber,
            ModelNumber = ModelNumber,
            DeviceClass = DeviceClass,
            Role = Role
        };
    }
}
