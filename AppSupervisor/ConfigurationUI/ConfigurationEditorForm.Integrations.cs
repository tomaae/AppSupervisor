using System.ComponentModel;
using AppSupervisor.HomeAssistant;
using AppSupervisor.SteamVr;

namespace AppSupervisor.ConfigurationUI;

/// <summary>Provides global integration configuration independently of the selected profile.</summary>
public sealed partial class ConfigurationEditorForm
{
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
    private readonly Func<CancellationToken, Task<SteamVrSnapshot>> _steamVrDeviceLoader;
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
        MultiSelect = true,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        RowHeadersVisible = false
    };
    private Button _discoverSteamVrDevicesButton = null!;

    private TabPage BuildIntegrationsPage()
    {
        var page = new TabPage("Integrations");
        var integrationsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 2
        };
        integrationsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        integrationsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        integrationsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        integrationsLayout.Controls.Add(BuildHomeAssistantIntegrationGroup(), 0, 0);
        var group = new GroupBox
        {
            Text = "Global — SteamVR device monitoring",
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            Margin = new Padding(0, 10, 0, 0)
        };
        TableLayoutPanel settings = CreateEditorTable();
        settings.Padding = new Padding(0, 4, 0, 8);
        AddSpanningEditorRow(settings, _steamVrEnabled);
        AddEditorRow(settings, "Timing", new Label
        {
            Text = "30-second startup grace, checks every 30 seconds, offline after 2 failed checks",
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
        integrationsLayout.Controls.Add(group, 0, 1);
        page.Controls.Add(integrationsLayout);

        LoadHomeAssistantIntegration();
        LoadSteamVrIntegration();
        _steamVrEnabled.CheckedChanged += SteamVrSettingsChanged;
        _steamVrReminderMinutes.ValueChanged += SteamVrSettingsChanged;
        _steamVrReminderMinutes.TextChanged += SteamVrSettingsChanged;
        _steamVrNotifications.TargetsChanged += SteamVrSettingsChanged;
        _steamVrDevices.CellValueChanged += SteamVrDeviceCellValueChanged;
        _steamVrDevices.CurrentCellDirtyStateChanged += SteamVrDeviceCellDirtyStateChanged;
        return page;
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
                    "SteamVR is running, but it did not expose any trackers or tracking references.",
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
        public string Status { get; set; } = "Not checked";
        public string ClassText => DeviceClass == SteamVrDeviceClass.TrackingReference
            ? "Base station"
            : "Tracker";

        public static SteamVrDeviceEditorRow FromConfig(SteamVrDeviceConfig device) => new()
        {
            Enabled = device.Enabled,
            Name = device.Name,
            SerialNumber = device.SerialNumber,
            ModelNumber = device.ModelNumber,
            DeviceClass = device.DeviceClass
        };

        public static SteamVrDeviceEditorRow FromSnapshot(SteamVrDeviceSnapshot device) => new()
        {
            Enabled = true,
            Name = string.IsNullOrWhiteSpace(device.ModelNumber)
                ? $"{(device.DeviceClass == SteamVrDeviceClass.TrackingReference ? "Base station" : "Tracker")} {device.SerialNumber}"
                : device.ModelNumber,
            SerialNumber = device.SerialNumber,
            ModelNumber = device.ModelNumber,
            DeviceClass = device.DeviceClass,
            Status = device.Connected ? "Connected" : "Offline"
        };

        public SteamVrDeviceConfig ToConfig() => new()
        {
            Enabled = Enabled,
            Name = Name,
            SerialNumber = SerialNumber,
            ModelNumber = ModelNumber,
            DeviceClass = DeviceClass
        };
    }
}
