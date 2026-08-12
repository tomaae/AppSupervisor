using AppSupervisor.Configuration;

namespace AppSupervisor.ConfigurationUI;

/// <summary>
/// Edits one listener or VRChat OSCQuery health check with type-specific fields and validation.
/// </summary>
public sealed partial class HealthCheckEditorDialog : Form
{
    private readonly CheckBox _enabledCheckBox = new() { Text = "Enabled", AutoSize = true };
    private readonly TextBox _nameTextBox = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _typeComboBox = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Dock = DockStyle.Fill
    };
    private readonly NumericUpDown _intervalSeconds = CreatePositiveNumeric(1, ConfigurationLimits.MaximumHealthIntervalSeconds);
    private readonly NumericUpDown _timeoutSeconds = CreatePositiveNumeric(1, ConfigurationLimits.MaximumHealthProbeTimeoutSeconds);
    private readonly NumericUpDown _failureThreshold = CreatePositiveNumeric(1, ConfigurationLimits.MaximumHealthFailureThreshold);
    private readonly NumericUpDown _startupDelaySeconds = CreatePositiveNumeric(0, ConfigurationLimits.MaximumHealthStartupDelaySeconds);
    private readonly CheckBox _restartOnFailureCheckBox = new()
    {
        Text = "Gracefully restart this helper after a confirmed failure",
        AutoSize = true
    };
    private readonly ComboBox _protocolComboBox = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 150
    };
    private readonly NumericUpDown _portNumeric = CreatePositiveNumeric(1, 65535);
    private readonly TextBox _activeWhenProcessTextBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _parametersTextBox = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        AcceptsReturn = true,
        Height = 150
    };
    private readonly CheckBox _staleEnabledCheckBox = new()
    {
        Text = "Fail when a majority remain unchanged for",
        AutoSize = true
    };
    private readonly NumericUpDown _staleSeconds = CreatePositiveNumeric(1, ConfigurationLimits.MaximumHealthStaleSeconds);
    private readonly NotificationTargetsControl _notificationTargets = new();
    private GroupBox _listenerGroup = null!;
    private GroupBox _vrcoscGroup = null!;

    /// <summary>Creates a detached editor for an existing or newly created health check.</summary>
    /// <param name="configuration">The health check used to initialize the dialog.</param>
    public HealthCheckEditorDialog(HealthCheckConfig configuration)
    {
        Text = "Health check";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(680, 720);
        Size = new Size(760, 820);
        AutoScaleMode = AutoScaleMode.Dpi;

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Control commonGroup = BuildCommonGroup();
        _listenerGroup = BuildListenerGroup();
        _vrcoscGroup = BuildVrcOscGroup();
        Control notificationsGroup = BuildNotificationsGroup();
        content.Controls.Add(commonGroup, 0, 0);
        content.Controls.Add(_listenerGroup, 0, 1);
        content.Controls.Add(_vrcoscGroup, 0, 2);
        content.Controls.Add(notificationsGroup, 0, 3);

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10)
        };
        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true
        };
        var saveButton = new Button
        {
            Text = "Save check",
            AutoSize = true
        };
        saveButton.Click += SaveClicked;
        buttonPanel.Controls.Add(cancelButton);
        buttonPanel.Controls.Add(saveButton);

        Controls.Add(content);
        Controls.Add(buttonPanel);
        AcceptButton = saveButton;
        CancelButton = cancelButton;
        _typeComboBox.FormattingEnabled = true;
        _protocolComboBox.FormattingEnabled = true;
        _typeComboBox.Format += TypeComboBoxFormat;
        _protocolComboBox.Format += ProtocolComboBoxFormat;
        _typeComboBox.DataSource = Enum.GetValues<HealthCheckType>();
        _protocolComboBox.DataSource = Enum.GetValues<ListenerProtocol>();
        _typeComboBox.SelectedValueChanged += TypeChanged;
        _staleEnabledCheckBox.CheckedChanged += StaleEnabledChanged;
        LoadConfiguration(configuration);
    }

    /// <summary>Gets the validated detached health check after the dialog is accepted.</summary>
    public HealthCheckConfig? Result { get; private set; }

    /// <summary>Builds the common identity, scheduling, failure confirmation, startup delay, and recovery action fields.</summary>
    /// <returns>The common settings group.</returns>
    private Control BuildCommonGroup()
    {
        var group = CreateGroup("General");
        TableLayoutPanel layout = CreateSettingsTable();
        AddSpanningRow(layout, _enabledCheckBox);
        AddRow(layout, "Name", _nameTextBox);
        AddRow(layout, "Type", _typeComboBox);
        AddSecondsRow(layout, "Check interval", _intervalSeconds);
        AddSecondsRow(layout, "Probe timeout", _timeoutSeconds);
        AddRow(layout, "Failures before unhealthy", _failureThreshold);
        AddSecondsRow(layout, "Startup delay", _startupDelaySeconds);
        AddRow(layout, "Recovery", _restartOnFailureCheckBox);
        group.Controls.Add(layout);
        return group;
    }

    /// <summary>Builds listener-only protocol, port, and optional process-gating fields.</summary>
    /// <returns>The listener settings group.</returns>
    private GroupBox BuildListenerGroup()
    {
        GroupBox group = CreateGroup("Listener");
        TableLayoutPanel layout = CreateSettingsTable();
        AddRow(layout, "Protocol", _protocolComboBox);
        AddRow(layout, "Port", _portNumeric);

        var processPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Margin = Padding.Empty
        };
        processPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        processPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var pickButton = new Button
        {
            Text = "Pick running...",
            AutoSize = true,
            Margin = new Padding(8, 0, 0, 0)
        };
        pickButton.Click += PickActivationProcessClicked;
        processPanel.Controls.Add(_activeWhenProcessTextBox, 0, 0);
        processPanel.Controls.Add(pickButton, 1, 0);
        AddRow(layout, "Only check while process runs", processPanel);
        AddSpanningRow(layout, new Label
        {
            AutoSize = true,
            MaximumSize = new Size(470, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Leave the process field empty to check whenever the helper and its profile are active. The listener address is intentionally unrestricted."
        });
        group.Controls.Add(layout);
        return group;
    }

    /// <summary>Builds vrcosc-only parameter presence and optional freshness fields.</summary>
    /// <returns>The VRChat OSCQuery settings group.</returns>
    private GroupBox BuildVrcOscGroup()
    {
        GroupBox group = CreateGroup("VRChat OSCQuery");
        TableLayoutPanel layout = CreateSettingsTable();
        AddRow(layout, "Runs only while", new Label
        {
            AutoSize = true,
            Text = "VRChat.exe is running"
        });
        AddSpanningRow(layout, new Label
        {
            AutoSize = true,
            MaximumSize = new Size(610, 0),
            Text = "OSCQuery discovers VRChat's HTTP and OSC endpoints automatically; no address, port, or protocol is configured."
        });
        AddRow(layout, "Parameter leaf names", _parametersTextBox);
        AddSpanningRow(layout, new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = "One name per line. Leave empty to check only the OSCQuery service and root address structure."
        });

        var stalePanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty
        };
        stalePanel.Controls.Add(_staleEnabledCheckBox);
        stalePanel.Controls.Add(_staleSeconds);
        stalePanel.Controls.Add(new Label
        {
            Text = "seconds",
            AutoSize = true,
            Margin = new Padding(4, 7, 0, 0)
        });
        AddRow(layout, "Freshness", stalePanel);
        AddSpanningRow(layout, new Label
        {
            AutoSize = true,
            MaximumSize = new Size(610, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Freshness requires at least two parameters and fails when a strict majority stay unchanged for the selected period."
        });
        group.Controls.Add(layout);
        return group;
    }

    /// <summary>Builds the independent per-check notification target selector.</summary>
    /// <returns>The notification settings group.</returns>
    private Control BuildNotificationsGroup()
    {
        GroupBox group = CreateGroup("Notifications");
        group.Dock = DockStyle.Top;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            Padding = new Padding(10)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _notificationTargets.Dock = DockStyle.Top;
        _notificationTargets.Margin = Padding.Empty;
        layout.Controls.Add(_notificationTargets, 0, 0);
        group.Controls.Add(layout);
        return group;
    }

    /// <summary>Loads an existing configuration into every editor control.</summary>
    /// <param name="configuration">The source health check.</param>
    private void LoadConfiguration(HealthCheckConfig configuration)
    {
        _enabledCheckBox.Checked = configuration.Enabled;
        _nameTextBox.Text = configuration.Name;
        _typeComboBox.SelectedItem = configuration.Type ?? HealthCheckType.Listener;
        _intervalSeconds.Value = Clamp(configuration.IntervalSeconds, _intervalSeconds);
        _timeoutSeconds.Value = Clamp(configuration.TimeoutSeconds, _timeoutSeconds);
        _failureThreshold.Value = Clamp(configuration.FailureThreshold, _failureThreshold);
        _startupDelaySeconds.Value = Clamp(
            configuration.StartupDelaySeconds,
            _startupDelaySeconds
        );
        _restartOnFailureCheckBox.Checked = configuration.RestartOnFailure;
        _protocolComboBox.SelectedItem = configuration.Protocol ?? ListenerProtocol.Tcp;
        _portNumeric.Value = Clamp(configuration.Port ?? 12345, _portNumeric);
        _activeWhenProcessTextBox.Text = configuration.ActiveWhenProcess;
        _parametersTextBox.Lines = configuration.Parameters.ToArray();
        _staleEnabledCheckBox.Checked = configuration.StaleSeconds is not null;
        _staleSeconds.Value = Clamp(configuration.StaleSeconds ?? 25, _staleSeconds);
        _notificationTargets.LoadTargets(configuration.Notifications.Target);
        UpdateTypeState();
        UpdateStaleState();
    }

    /// <summary>Updates visible type-specific controls when the selected health-check type changes.</summary>
    /// <param name="sender">The type combo box.</param>
    /// <param name="e">The selection-change event data.</param>
    private void TypeChanged(object? sender, EventArgs e) => UpdateTypeState();

    /// <summary>Updates the stale-seconds input when freshness detection is toggled.</summary>
    /// <param name="sender">The freshness check box.</param>
    /// <param name="e">The checked-change event data.</param>
    private void StaleEnabledChanged(object? sender, EventArgs e) => UpdateStaleState();

    /// <summary>Enables only the settings applicable to the selected health-check type.</summary>
    private void UpdateTypeState()
    {
        bool listener = _typeComboBox.SelectedItem is HealthCheckType.Listener;
        _listenerGroup.Visible = listener;
        _vrcoscGroup.Visible = !listener;
    }

    /// <summary>Enables stale duration only while vrcosc freshness detection is selected.</summary>
    private void UpdateStaleState()
    {
        _staleSeconds.Enabled = _staleEnabledCheckBox.Checked;
    }

    /// <summary>Lets the user choose the optional listener activation process from running processes.</summary>
    /// <param name="sender">The Pick running button.</param>
    /// <param name="e">The click event data.</param>
    private void PickActivationProcessClicked(object? sender, EventArgs e)
    {
        using var picker = new RunningProcessPickerDialog();

        if (picker.ShowDialog(this) == DialogResult.OK)
            _activeWhenProcessTextBox.Text = picker.SelectedProcessName ?? "";
    }

    /// <summary>Builds, validates, and returns the edited health check.</summary>
    /// <param name="sender">The Save check button.</param>
    /// <param name="e">The click event data.</param>
    private void SaveClicked(object? sender, EventArgs e)
    {
        HealthCheckConfig candidate = BuildConfiguration();

        try
        {
            ValidateCandidate(candidate);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Health check is invalid",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
            return;
        }

        Result = candidate;
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>Creates a detached configuration from the current form values and clears fields forbidden by its type.</summary>
    /// <returns>The candidate health check.</returns>
    private HealthCheckConfig BuildConfiguration()
    {
        var type = (HealthCheckType)_typeComboBox.SelectedItem!;
        string[] parameters = _parametersTextBox.Lines
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();

        return new HealthCheckConfig
        {
            Enabled = _enabledCheckBox.Checked,
            Name = _nameTextBox.Text.Trim(),
            Type = type,
            Protocol = type == HealthCheckType.Listener
                ? (ListenerProtocol)_protocolComboBox.SelectedItem!
                : null,
            Port = type == HealthCheckType.Listener
                ? Decimal.ToInt32(_portNumeric.Value)
                : null,
            ActiveWhenProcess = type == HealthCheckType.Listener
                ? _activeWhenProcessTextBox.Text.Trim()
                : "",
            IntervalSeconds = Decimal.ToInt32(_intervalSeconds.Value),
            TimeoutSeconds = Decimal.ToInt32(_timeoutSeconds.Value),
            FailureThreshold = Decimal.ToInt32(_failureThreshold.Value),
            StartupDelaySeconds = Decimal.ToInt32(_startupDelaySeconds.Value),
            RestartOnFailure = _restartOnFailureCheckBox.Checked,
            Parameters = type == HealthCheckType.Vrcosc ? [.. parameters] : [],
            StaleSeconds = type == HealthCheckType.Vrcosc && _staleEnabledCheckBox.Checked
                ? Decimal.ToInt32(_staleSeconds.Value)
                : null,
            Notifications = new NotificationConfig
            {
                Target = [.. _notificationTargets.SelectedTargets]
            }
        };
    }

    /// <summary>Uses the production validator against an otherwise valid temporary profile and helper.</summary>
    /// <param name="candidate">The health check to validate.</param>
    private static void ValidateCandidate(HealthCheckConfig candidate)
    {
        var profile = new SupervisorProfileConfig
        {
            Name = "Health check editor",
            MonitorProcess = "notepad.exe",
            Applications =
            [
                new ManagedApplicationConfig
                {
                    Path = Environment.ProcessPath
                        ?? throw new InvalidOperationException("The editor executable path is unavailable."),
                    Notifications = new NotificationConfig { Target = [] },
                    HealthChecks = [candidate]
                }
            ],
            Services = []
        };
        ConfigValidator.Validate([profile]);
    }

    /// <summary>Creates a standard two-column settings table.</summary>
    /// <returns>A table with label and editor columns.</returns>
    private static TableLayoutPanel CreateSettingsTable()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(10)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return layout;
    }

    /// <summary>Adds one label/editor row to a settings table.</summary>
    /// <param name="layout">The target settings table.</param>
    /// <param name="labelText">The row label, or empty text for an unlabeled spanning-style row.</param>
    /// <param name="control">The editor or explanatory control.</param>
    private static void AddRow(
        TableLayoutPanel layout,
        string labelText,
        Control control)
    {
        int row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = labelText,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 7, 10, 4)
        }, 0, row);
        control.Margin = new Padding(0, 3, 0, 4);
        layout.Controls.Add(control, 1, row);
    }

    /// <summary>Adds a numeric seconds row with an explicit unit suffix.</summary>
    /// <param name="layout">The target settings table.</param>
    /// <param name="labelText">The row label.</param>
    /// <param name="numeric">The seconds input.</param>
    private static void AddSecondsRow(
        TableLayoutPanel layout,
        string labelText,
        NumericUpDown numeric)
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
            Margin = new Padding(4, 7, 0, 0)
        });
        AddRow(layout, labelText, panel);
    }

    /// <summary>Creates a consistently padded group box that fills the editor's shared content column.</summary>
    /// <param name="text">The group title.</param>
    /// <returns>The new group box.</returns>
    private static GroupBox CreateGroup(string text)
    {
        return new GroupBox
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 10)
        };
    }

    /// <summary>Creates a bounded integer numeric input with a practical editor width.</summary>
    /// <param name="minimum">The inclusive minimum.</param>
    /// <param name="maximum">The inclusive maximum.</param>
    /// <returns>The numeric input.</returns>
    private static NumericUpDown CreatePositiveNumeric(int minimum, int maximum)
    {
        return new NumericUpDown
        {
            Minimum = minimum,
            Maximum = maximum,
            Width = 100
        };
    }

    /// <summary>Clamps an integer model value to a numeric input's supported range.</summary>
    /// <param name="value">The model value.</param>
    /// <param name="numeric">The target numeric control.</param>
    /// <returns>The clamped decimal value.</returns>
    private static decimal Clamp(int value, NumericUpDown numeric)
    {
        return Math.Clamp((decimal)value, numeric.Minimum, numeric.Maximum);
    }
}
