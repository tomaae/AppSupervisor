using System.ComponentModel;
using AppSupervisor.Bluetooth;

namespace AppSupervisor.ConfigurationUI;

/// <summary>Provides global Bluetooth registration and profile presence-trigger editing.</summary>
public sealed partial class ConfigurationEditorForm
{
    private readonly IBluetoothDeviceScanner _bluetoothDeviceScanner;
    private readonly ComboBox _profileTriggerType = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
        FormattingEnabled = true
    };
    private readonly ComboBox _monitorBluetoothDevice = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
        FormattingEnabled = true
    };
    private readonly NumericUpDown _bluetoothScanInterval = new()
    {
        Minimum = 10,
        Maximum = 300,
        Width = 100
    };
    private readonly NumericUpDown _bluetoothPresenceTimeout = new()
    {
        Minimum = 10,
        Maximum = 900,
        Width = 100
    };
    private readonly BindingList<BluetoothDeviceEditorRow> _bluetoothDeviceRows = [];
    private readonly CancellationTokenSource _bluetoothDiscoveryCancellation = new();
    private bool _bluetoothDiscoveryDisposed;
    private readonly DataGridView _bluetoothDevices = new()
    {
        Dock = DockStyle.Fill,
        AutoGenerateColumns = false,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        MultiSelect = true,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        RowHeadersVisible = false
    };
    private Button _discoverBluetoothDevicesButton = null!;

    private Control BuildProfileTriggerSelector()
    {
        _profileTriggerType.Items.AddRange(
            Enum.GetValues<ProfileTriggerType>().Cast<object>().ToArray()
        );
        _profileTriggerType.Format += (_, args) =>
        {
            if (args.ListItem is ProfileTriggerType triggerType)
            {
                args.Value = triggerType == ProfileTriggerType.Process
                    ? "Running process"
                    : "Bluetooth device presence";
            }
        };
        _monitorBluetoothDevice.Format += (_, args) =>
        {
            if (args.ListItem is BluetoothDeviceEditorRow row)
                args.Value = $"{row.Name} ({row.KindText}, {row.FormattedAddress})";
        };
        return _profileTriggerType;
    }

    private void BindProfileBluetoothDeviceSelector(string? preferredDeviceId)
    {
        string selectedId = preferredDeviceId?.Trim() ?? "";
        _monitorBluetoothDevice.Items.Clear();

        foreach (BluetoothDeviceEditorRow row in _bluetoothDeviceRows)
            _monitorBluetoothDevice.Items.Add(row);

        BluetoothDeviceEditorRow? selected = _bluetoothDeviceRows.FirstOrDefault(row =>
            string.Equals(row.DeviceId, selectedId, StringComparison.OrdinalIgnoreCase)
        );
        _monitorBluetoothDevice.SelectedItem = selected;
    }

    private void RefreshProfileBluetoothDeviceSelector()
    {
        string preferred = SelectedProfile?.MonitorBluetoothDeviceId ?? "";
        bool wasLoading = _loadingControls;
        _loadingControls = true;

        try
        {
            BindProfileBluetoothDeviceSelector(preferred);
        }
        finally
        {
            _loadingControls = wasLoading;
        }

        if (!wasLoading &&
            SelectedProfile?.TriggerType == ProfileTriggerType.BluetoothDevice &&
            _monitorBluetoothDevice.SelectedItem is null &&
            _monitorBluetoothDevice.Items.Count > 0)
        {
            _monitorBluetoothDevice.SelectedIndex = 0;
        }
    }

    private void UpdateProfileTriggerControlAvailability()
    {
        bool processTrigger = _profileTriggerType.SelectedItem is not ProfileTriggerType type ||
            type == ProfileTriggerType.Process;
        _monitorProcessPanel.Enabled = processTrigger;
        _monitorBluetoothDevice.Enabled = !processTrigger &&
            _monitorBluetoothDevice.Items.Count > 0;
    }

    private Control BuildBluetoothIntegrationGroup()
    {
        var group = new GroupBox
        {
            Text = "Global — Bluetooth device presence",
            Dock = DockStyle.Top,
            MinimumSize = new Size(0, 410),
            Padding = new Padding(12),
            Margin = new Padding(0, 10, 0, 0)
        };
        TableLayoutPanel settings = CreateEditorTable();
        settings.Padding = new Padding(0, 4, 0, 8);
        AddEditorRow(settings, "Scan interval", BuildSecondsEditor(_bluetoothScanInterval));
        AddEditorRow(settings, "Presence timeout", BuildSecondsEditor(_bluetoothPresenceTimeout));
        AddEditorRow(settings, "", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(720, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Profiles can activate from a registered device. Connected devices, BLE advertisements, and discoverable Bluetooth Classic devices count as present; the timeout prevents brief missed scans from closing a profile."
        });

        ConfigureBluetoothGrid();
        var devicePanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 6, 0, 0)
        };
        _discoverBluetoothDevicesButton = CreateButton(
            "Discover nearby devices",
            DiscoverBluetoothDevicesClicked
        );
        buttons.Controls.Add(_discoverBluetoothDevicesButton);
        buttons.Controls.Add(CreateButton("Remove selected", RemoveBluetoothDevicesClicked));
        devicePanel.Controls.Add(_bluetoothDevices);
        devicePanel.Controls.Add(buttons);
        group.Controls.Add(devicePanel);
        group.Controls.Add(settings);
        return group;
    }

    private static Control BuildSecondsEditor(NumericUpDown numeric)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty
        };
        numeric.Margin = Padding.Empty;
        panel.Controls.Add(numeric);
        panel.Controls.Add(new Label
        {
            Text = "seconds",
            AutoSize = true,
            Margin = new Padding(6, 5, 0, 0)
        });
        return panel;
    }

    private void ConfigureBluetoothGrid()
    {
        _bluetoothDevices.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(BluetoothDeviceEditorRow.Name),
            HeaderText = "Registered name",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 130
        });
        _bluetoothDevices.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(BluetoothDeviceEditorRow.KindText),
            HeaderText = "Type",
            ReadOnly = true,
            Width = 95
        });
        _bluetoothDevices.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(BluetoothDeviceEditorRow.FormattedAddress),
            HeaderText = "Address",
            ReadOnly = true,
            Width = 135
        });
        _bluetoothDevices.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(BluetoothDeviceEditorRow.Status),
            HeaderText = "Last discovery",
            ReadOnly = true,
            Width = 115
        });
        _bluetoothDevices.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(BluetoothDeviceEditorRow.Signal),
            HeaderText = "Signal",
            ReadOnly = true,
            Width = 75
        });
        _bluetoothDevices.DataSource = _bluetoothDeviceRows;
    }

    private void LoadBluetoothIntegration()
    {
        BluetoothIntegrationConfig configuration = _configuration.Integrations.Bluetooth;
        _bluetoothScanInterval.Value = Math.Clamp(
            configuration.ScanIntervalSeconds,
            Decimal.ToInt32(_bluetoothScanInterval.Minimum),
            Decimal.ToInt32(_bluetoothScanInterval.Maximum)
        );
        _bluetoothPresenceTimeout.Value = Math.Clamp(
            configuration.PresenceTimeoutSeconds,
            Decimal.ToInt32(_bluetoothPresenceTimeout.Minimum),
            Decimal.ToInt32(_bluetoothPresenceTimeout.Maximum)
        );

        foreach (BluetoothDeviceConfig device in configuration.Devices)
            _bluetoothDeviceRows.Add(BluetoothDeviceEditorRow.FromConfig(device));
    }

    private void BluetoothSettingsChanged(object? sender, EventArgs e)
    {
        if (_loadingControls)
            return;

        SaveBluetoothIntegrationFromControls();
        RefreshProfileBluetoothDeviceSelector();
        UpdateStatus();
    }

    private void BluetoothDeviceCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_loadingControls || e.RowIndex < 0)
            return;

        SaveBluetoothIntegrationFromControls();
        RefreshProfileBluetoothDeviceSelector();
        _profileSelector.Refresh();
        UpdateStatus();
    }

    private void SaveBluetoothIntegrationFromControls()
    {
        BluetoothIntegrationConfig configuration = _configuration.Integrations.Bluetooth;
        configuration.ScanIntervalSeconds = ReadDisplayedNumber(_bluetoothScanInterval);
        configuration.PresenceTimeoutSeconds = ReadDisplayedNumber(_bluetoothPresenceTimeout);
        configuration.Devices = _bluetoothDeviceRows.Select(row => row.ToConfig()).ToList();
    }

    private async void DiscoverBluetoothDevicesClicked(object? sender, EventArgs e)
    {
        _discoverBluetoothDevicesButton.Enabled = false;

        try
        {
            IReadOnlyList<BluetoothDeviceSnapshot> discovered =
                await _bluetoothDeviceScanner.DiscoverAsync(
                    _bluetoothDiscoveryCancellation.Token
                );

            bool wasLoading = _loadingControls;
            _loadingControls = true;

            try
            {
                foreach (BluetoothDeviceSnapshot device in discovered)
                {
                    BluetoothDeviceEditorRow? existing = _bluetoothDeviceRows.FirstOrDefault(row =>
                        row.Kind == device.Kind &&
                        string.Equals(row.Address, device.Address, StringComparison.OrdinalIgnoreCase)
                    );

                    if (existing is null)
                    {
                        _bluetoothDeviceRows.Add(BluetoothDeviceEditorRow.FromSnapshot(device));
                        continue;
                    }

                    existing.UpdateFromSnapshot(device);
                }
            }
            finally
            {
                _loadingControls = wasLoading;
            }

            _bluetoothDevices.Refresh();
            SaveBluetoothIntegrationFromControls();
            RefreshProfileBluetoothDeviceSelector();
            UpdateStatus();

            if (discovered.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "Windows completed Bluetooth discovery but did not report any nearby or paired devices.",
                    "No Bluetooth devices found",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
        }
        catch (OperationCanceledException) when (_bluetoothDiscoveryCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Bluetooth discovery failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }
        finally
        {
            if (!_discoverBluetoothDevicesButton.IsDisposed)
                _discoverBluetoothDevicesButton.Enabled = true;
        }
    }

    private void RemoveBluetoothDevicesClicked(object? sender, EventArgs e)
    {
        BluetoothDeviceEditorRow[] selected = _bluetoothDevices.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(row => row.DataBoundItem)
            .OfType<BluetoothDeviceEditorRow>()
            .ToArray();
        var selectedIds = selected.Select(row => row.DeviceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] referencedProfiles = _profiles
            .Where(profile => profile.TriggerType == ProfileTriggerType.BluetoothDevice &&
                selectedIds.Contains(profile.MonitorBluetoothDeviceId))
            .Select(profile => DisplayName(profile.Name, "Unnamed profile"))
            .ToArray();

        if (referencedProfiles.Length > 0)
        {
            MessageBox.Show(
                this,
                "Change these profiles to another activation trigger before removing the device: " +
                    string.Join(", ", referencedProfiles),
                "Bluetooth device is in use",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
            return;
        }

        foreach (BluetoothDeviceEditorRow row in selected)
            _bluetoothDeviceRows.Remove(row);

        if (selected.Length > 0)
        {
            SaveBluetoothIntegrationFromControls();
            RefreshProfileBluetoothDeviceSelector();
            UpdateStatus();
        }
    }

    private sealed class BluetoothDeviceEditorRow
    {
        public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public string Address { get; set; } = "";
        public BluetoothDeviceKind Kind { get; set; }
        public string Status { get; set; } = "Not checked";
        public string Signal { get; set; } = "—";
        public string KindText => Kind == BluetoothDeviceKind.Classic ? "Classic" : "Low Energy";
        public string FormattedAddress => string.Join(":", Enumerable.Range(0, 6)
            .Select(index => Address.Length == 12 ? Address.Substring(index * 2, 2) : "??"));

        public static BluetoothDeviceEditorRow FromConfig(BluetoothDeviceConfig device) => new()
        {
            DeviceId = device.DeviceId,
            Name = BluetoothDeviceScanner.HasUsableName(device.Name, device.Address)
                ? device.Name
                : CreateUnidentifiedName(device.Kind, device.Address),
            Address = device.Address,
            Kind = device.Kind
        };

        public static BluetoothDeviceEditorRow FromSnapshot(BluetoothDeviceSnapshot device) => new()
        {
            Name = BluetoothDeviceScanner.HasUsableName(device.Name, device.Address)
                ? device.Name
                : CreateUnidentifiedName(device.Kind, device.Address),
            Address = device.Address,
            Kind = device.Kind,
            Status = GetStatus(device),
            Signal = GetSignal(device)
        };

        public void UpdateFromSnapshot(BluetoothDeviceSnapshot device)
        {
            string generatedName = CreateUnidentifiedName(Kind, Address);
            Name = string.Equals(Name, generatedName, StringComparison.Ordinal)
                ? BluetoothDeviceScanner.ChoosePreferredName("", device.Name, Address)
                : BluetoothDeviceScanner.ChoosePreferredName(Name, device.Name, Address);
            if (Name.Length == 0)
                Name = generatedName;

            Status = GetStatus(device);
            Signal = GetSignal(device);
        }

        private static string CreateUnidentifiedName(BluetoothDeviceKind kind, string address)
        {
            string transport = kind == BluetoothDeviceKind.Classic ? "Classic" : "LE";
            string suffix = address.Length >= 4 ? address[^4..] : "????";
            return $"Unidentified {transport} device ({suffix})";
        }

        private static string GetSignal(BluetoothDeviceSnapshot device) =>
            device.SignalStrengthDbm is short signal ? $"{signal} dBm" : "—";

        public static string GetStatus(BluetoothDeviceSnapshot device) => device.IsConnected
            ? "Connected"
            : device.IsPresent
                ? "Nearby"
                : device.IsPaired
                    ? "Paired (offline)"
                    : "Not present";

        public BluetoothDeviceConfig ToConfig() => new()
        {
            DeviceId = DeviceId,
            Name = Name,
            Address = Address,
            Kind = Kind
        };
    }
}
