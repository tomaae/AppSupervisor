using AppSupervisor.Configuration;
using AppSupervisor.Mqtt;
using AppSupervisor.Resources;

namespace AppSupervisor.ConfigurationUI;

/// <summary>Provides global MQTT broker settings and profile publish-resource editing.</summary>
public sealed partial class ConfigurationEditorForm
{
    private readonly TextBox _mqttHost = new()
    {
        Name = "MqttHostTextBox",
        Dock = DockStyle.Fill
    };
    private readonly NumericUpDown _mqttPort = new()
    {
        Name = "MqttPortNumeric",
        Minimum = 1,
        Maximum = 65_535,
        Width = 110
    };
    private readonly CheckBox _mqttTls = new()
    {
        Name = "MqttTlsCheckBox",
        Text = "Use TLS (validate the broker certificate)",
        AutoSize = true
    };
    private readonly TextBox _mqttUsername = new()
    {
        Name = "MqttUsernameTextBox",
        Dock = DockStyle.Fill
    };
    private readonly TextBox _mqttPassword = new()
    {
        Name = "MqttPasswordTextBox",
        Dock = DockStyle.Fill,
        UseSystemPasswordChar = true
    };
    private Button _testMqttButton = null!;

    private readonly Panel _mqttEditorPanel = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _mqttEnabled = new()
    {
        Text = "MQTT publish enabled",
        AutoSize = true
    };
    private readonly TextBox _mqttTopic = new()
    {
        Name = "MqttTopicTextBox",
        Dock = DockStyle.Fill
    };
    private readonly TextBox _mqttPayload = CreateMqttPayloadEditor("MqttPayloadTextBox");
    private readonly ComboBox _mqttQos = CreateMqttSelector("MqttQosComboBox");
    private readonly CheckBox _mqttRetain = new()
    {
        Text = "Retain activation payload",
        AutoSize = true
    };
    private readonly CheckBox _mqttVerify = new()
    {
        Text = "Verify activation state",
        AutoSize = true
    };
    private readonly TextBox _mqttVerificationTopic = new()
    {
        Name = "MqttVerificationTopicTextBox",
        Dock = DockStyle.Fill
    };
    private readonly TextBox _mqttExpectedState = CreateMqttPayloadEditor(
        "MqttExpectedStateTextBox"
    );
    private readonly NumericUpDown _mqttVerificationTimeout = new()
    {
        Name = "MqttVerificationTimeoutNumeric",
        Minimum = 1,
        Maximum = 300,
        Width = 90
    };
    private readonly ComboBox _mqttDeactivationBehavior = CreateMqttSelector(
        "MqttDeactivationBehaviorComboBox"
    );
    private readonly Panel _mqttDeactivationOptions = new()
    {
        Dock = DockStyle.Fill,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink
    };
    private readonly TextBox _mqttDeactivationTopic = new()
    {
        Name = "MqttDeactivationTopicTextBox",
        Dock = DockStyle.Fill
    };
    private Label _mqttDeactivationPayloadLabel = null!;
    private readonly TextBox _mqttDeactivationPayload = CreateMqttPayloadEditor(
        "MqttDeactivationPayloadTextBox"
    );
    private readonly ComboBox _mqttDeactivationQos = CreateMqttSelector(
        "MqttDeactivationQosComboBox"
    );
    private readonly CheckBox _mqttDeactivationRetain = new()
    {
        Text = "Retain inverse payload",
        AutoSize = true
    };
    private readonly CheckBox _mqttVerifyDeactivation = new()
    {
        Text = "Verify configured inverse state",
        AutoSize = true
    };
    private Label _mqttDeactivationExpectedLabel = null!;
    private readonly TextBox _mqttDeactivationExpectedState = CreateMqttPayloadEditor(
        "MqttDeactivationExpectedStateTextBox"
    );
    private readonly NotificationTargetsControl _mqttNotifications = new();
    private readonly Label _mqttStatus = new()
    {
        AutoSize = true,
        ForeColor = SystemColors.GrayText
    };
    private Button _testMqttActionButton = null!;
    private bool _mqttActionTestPending;

    private Control BuildMqttIntegrationGroup()
    {
        var group = new GroupBox
        {
            Text = "Global — MQTT broker",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = new Padding(0, 10, 0, 0)
        };
        TableLayoutPanel layout = CreateEditorTable();
        AddEditorRow(layout, "Host", _mqttHost);
        AddEditorRow(layout, "Port", _mqttPort);
        AddEditorRow(layout, "Transport", _mqttTls);
        AddEditorRow(layout, "Username", _mqttUsername);
        AddEditorRow(layout, "Password", _mqttPassword);
        _testMqttButton = CreateButton("Test connection", TestMqttConnectionClicked);
        _testMqttButton.Name = "TestMqttConnectionButton";
        AddEditorRow(layout, "", _testMqttButton);
        AddEditorRow(layout, "", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(720, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Uses MQTT 3.1.1 over TCP. TLS uses Windows certificate validation; " +
                "there is no insecure certificate bypass. The password is masked here but " +
                "stored in the local configuration file."
        });
        group.Controls.Add(layout);
        return group;
    }

    private void LoadMqttIntegration()
    {
        MqttIntegrationConfig configuration = _configuration.Integrations.Mqtt;
        _mqttHost.Text = configuration.Host;
        _mqttPort.Value = Math.Clamp(configuration.Port, 1, 65_535);
        _mqttTls.Checked = configuration.UseTls;
        _mqttUsername.Text = configuration.Username;
        _mqttPassword.Text = configuration.Password;
        _mqttHost.TextChanged += MqttIntegrationFieldChanged;
        _mqttPort.ValueChanged += MqttIntegrationFieldChanged;
        _mqttPort.TextChanged += MqttIntegrationFieldChanged;
        _mqttTls.CheckedChanged += MqttIntegrationFieldChanged;
        _mqttUsername.TextChanged += MqttIntegrationFieldChanged;
        _mqttPassword.TextChanged += MqttIntegrationFieldChanged;
    }

    private void MqttIntegrationFieldChanged(object? sender, EventArgs e)
    {
        if (_loadingControls)
            return;

        MqttIntegrationConfig configuration = _configuration.Integrations.Mqtt;
        configuration.Host = _mqttHost.Text;
        configuration.Port = ReadDisplayedNumber(_mqttPort);
        configuration.UseTls = _mqttTls.Checked;
        configuration.Username = _mqttUsername.Text;
        configuration.Password = _mqttPassword.Text;
        UpdateStatus();
    }

    private async void TestMqttConnectionClicked(object? sender, EventArgs e)
    {
        _testMqttButton.Enabled = false;

        try
        {
            MqttIntegrationConfig configuration = ConfigJson.Clone(
                _configuration.Integrations.Mqtt
            );
            IntegrationConfigValidator.Validate(new IntegrationsConfig
            {
                Mqtt = configuration
            });
            using var client = new MqttBrokerClient(configuration);
            await client.TestConnectionAsync(CancellationToken.None);

            if (!IsDisposed)
            {
                MessageBox.Show(
                    this,
                    $"Connected to MQTT broker {configuration.Host}:{configuration.Port}.",
                    "MQTT connection succeeded",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
        }
        catch (Exception exception)
        {
            if (!IsDisposed)
            {
                MessageBox.Show(
                    this,
                    exception.Message,
                    "MQTT connection failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
        }
        finally
        {
            if (!_testMqttButton.IsDisposed)
                _testMqttButton.Enabled = true;
        }
    }

    private Control BuildMqttEditor()
    {
        _mqttEditorPanel.Padding = new Padding(14);
        var scrolling = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        TableLayoutPanel layout = CreateEditorTable();
        ConfigureMqttSelectors();
        AddEditorRow(layout, "", _mqttEnabled);
        AddEditorRow(layout, "Topic", _mqttTopic);
        AddEditorRow(layout, "Payload", _mqttPayload);
        AddEditorRow(layout, "QoS", _mqttQos);
        AddEditorRow(layout, "", _mqttRetain);
        AddEditorRow(layout, "", _mqttVerify);
        AddEditorRow(layout, "State topic", _mqttVerificationTopic);
        AddEditorRow(layout, "Expected state", _mqttExpectedState);
        AddEditorRow(
            layout,
            "State timeout",
            BuildNumberUnitEditor(_mqttVerificationTimeout, "seconds")
        );
        AddEditorRow(layout, "On deactivation", _mqttDeactivationBehavior);
        BuildMqttDeactivationOptions();
        AddSpanningEditorRow(layout, _mqttDeactivationOptions);
        _testMqttActionButton = CreateButton("Test action", TestMqttActionClicked);
        _testMqttActionButton.Name = "TestMqttActionButton";
        AddEditorRow(layout, "Action", _testMqttActionButton);
        AddEditorRow(layout, "Notifications", BuildNotificationTestPanel(
            _mqttNotifications,
            TestMqttNotificationClicked
        ));
        AddEditorRow(layout, "", _mqttStatus);
        AddEditorRow(layout, "", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            ForeColor = SystemColors.GrayText,
            Text = "One-shot leaves the published state in place. Configured inverse publishes " +
                "the exact reverse message you provide. Retained-state restoration refuses to " +
                "activate unless it first captures a retained state, then restores and verifies " +
                "those exact bytes on deactivation."
        });
        scrolling.Controls.Add(layout);
        _mqttEditorPanel.Controls.Add(scrolling);

        _mqttEnabled.CheckedChanged += MqttResourceFieldChanged;
        _mqttTopic.TextChanged += MqttResourceFieldChanged;
        _mqttPayload.TextChanged += MqttResourceFieldChanged;
        _mqttQos.SelectedIndexChanged += MqttResourceFieldChanged;
        _mqttRetain.CheckedChanged += MqttResourceFieldChanged;
        _mqttVerify.CheckedChanged += MqttResourceOptionChanged;
        _mqttVerificationTopic.TextChanged += MqttResourceFieldChanged;
        _mqttExpectedState.TextChanged += MqttResourceFieldChanged;
        _mqttVerificationTimeout.ValueChanged += MqttResourceFieldChanged;
        _mqttVerificationTimeout.TextChanged += MqttResourceFieldChanged;
        _mqttDeactivationBehavior.SelectedIndexChanged += MqttResourceOptionChanged;
        _mqttDeactivationTopic.TextChanged += MqttResourceFieldChanged;
        _mqttDeactivationPayload.TextChanged += MqttResourceFieldChanged;
        _mqttDeactivationQos.SelectedIndexChanged += MqttResourceFieldChanged;
        _mqttDeactivationRetain.CheckedChanged += MqttResourceFieldChanged;
        _mqttVerifyDeactivation.CheckedChanged += MqttResourceOptionChanged;
        _mqttDeactivationExpectedState.TextChanged += MqttResourceFieldChanged;
        _mqttNotifications.TargetsChanged += MqttResourceFieldChanged;
        return _mqttEditorPanel;
    }

    private void ConfigureMqttSelectors()
    {
        _mqttQos.Items.AddRange(Enum.GetValues<MqttQualityOfService>().Cast<object>().ToArray());
        _mqttDeactivationQos.Items.AddRange(
            Enum.GetValues<MqttQualityOfService>().Cast<object>().ToArray()
        );
        _mqttDeactivationBehavior.Items.AddRange(
            Enum.GetValues<MqttDeactivationBehavior>().Cast<object>().ToArray()
        );
        ListControl[] qosSelectors = [_mqttQos, _mqttDeactivationQos];

        foreach (ListControl selector in qosSelectors)
        {
            selector.Format += (_, args) =>
            {
                if (args.ListItem is MqttQualityOfService qos)
                    args.Value = FormatMqttQos(qos);
            };
        }

        _mqttDeactivationBehavior.Format += (_, args) =>
        {
            if (args.ListItem is MqttDeactivationBehavior behavior)
                args.Value = FormatMqttDeactivation(behavior);
        };
    }

    private void BuildMqttDeactivationOptions()
    {
        TableLayoutPanel layout = CreateEditorTable();
        layout.Dock = DockStyle.Top;
        AddEditorRow(layout, "Inverse topic", _mqttDeactivationTopic);
        _mqttDeactivationPayloadLabel = AddEditorRow(
            layout,
            "Reverse payload",
            _mqttDeactivationPayload
        );
        AddEditorRow(layout, "Inverse QoS", _mqttDeactivationQos);
        AddEditorRow(layout, "", _mqttDeactivationRetain);
        AddEditorRow(layout, "", _mqttVerifyDeactivation);
        _mqttDeactivationExpectedLabel = AddEditorRow(
            layout,
            "Expected inverse state",
            _mqttDeactivationExpectedState
        );
        _mqttDeactivationOptions.Controls.Add(layout);
    }

    private void LoadSelectedMqtt()
    {
        MqttResourceConfig? resource = SelectedMqtt;
        _loadingControls = true;

        try
        {
            _mqttEditorPanel.Enabled = resource is not null;
            _testMqttActionButton.Enabled = resource is not null && !_mqttActionTestPending;
            _mqttEnabled.Checked = resource?.Enabled ?? false;
            _mqttTopic.Text = resource?.Topic ?? "";
            _mqttPayload.Text = resource?.Payload ?? "";
            _mqttQos.SelectedItem = resource?.Qos ?? MqttQualityOfService.AtLeastOnce;
            _mqttRetain.Checked = resource?.Retain ?? false;
            _mqttVerify.Checked = resource?.VerifyStateChange ?? false;
            _mqttVerificationTopic.Text = resource?.VerificationTopic ?? "";
            _mqttExpectedState.Text = resource?.ExpectedState ?? "";
            _mqttVerificationTimeout.Value = resource is null
                ? _mqttVerificationTimeout.Minimum
                : Math.Clamp(resource.VerificationTimeoutSeconds, 1, 300);
            _mqttDeactivationBehavior.SelectedItem = resource?.DeactivationBehavior ??
                MqttDeactivationBehavior.OneShot;
            _mqttDeactivationTopic.Text = resource?.DeactivationTopic ?? "";
            _mqttDeactivationPayload.Text = resource?.DeactivationPayload ?? "";
            _mqttDeactivationQos.SelectedItem = resource?.DeactivationQos ??
                MqttQualityOfService.AtLeastOnce;
            _mqttDeactivationRetain.Checked = resource?.DeactivationRetain ?? false;
            _mqttVerifyDeactivation.Checked = resource?.VerifyDeactivation ?? false;
            _mqttDeactivationExpectedState.Text = resource?.DeactivationExpectedState ?? "";
            _mqttNotifications.LoadTargets(resource?.Notifications.Target ?? []);
            _mqttStatus.Text = "";
            UpdateMqttControlStates();
        }
        finally
        {
            _loadingControls = false;
        }
    }

    private void MqttResourceOptionChanged(object? sender, EventArgs e)
    {
        if (_loadingControls || SelectedMqtt is not MqttResourceConfig resource)
            return;

        MqttDeactivationBehavior behavior = _mqttDeactivationBehavior.SelectedItem is
            MqttDeactivationBehavior selected
                ? selected
                : MqttDeactivationBehavior.OneShot;

        if (behavior != MqttDeactivationBehavior.OneShot &&
            string.IsNullOrEmpty(_mqttDeactivationTopic.Text))
        {
            _mqttDeactivationTopic.Text = _mqttTopic.Text;
        }

        if (behavior == MqttDeactivationBehavior.RestoreRetainedState)
        {
            _mqttDeactivationRetain.Checked = true;
            _mqttVerifyDeactivation.Checked = false;
        }

        UpdateMqttControlStates();
        MqttResourceFieldChanged(sender, e);
    }

    private void UpdateMqttControlStates()
    {
        MqttDeactivationBehavior behavior = _mqttDeactivationBehavior.SelectedItem is
            MqttDeactivationBehavior selected
                ? selected
                : MqttDeactivationBehavior.OneShot;
        bool capturedRestore = behavior == MqttDeactivationBehavior.RestoreRetainedState;
        bool configuredInverse = behavior == MqttDeactivationBehavior.PublishConfiguredPayload;
        _mqttVerificationTopic.Enabled = _mqttVerify.Checked || capturedRestore ||
            (configuredInverse && _mqttVerifyDeactivation.Checked);
        _mqttExpectedState.Enabled = _mqttVerify.Checked;
        _mqttDeactivationOptions.Visible = behavior != MqttDeactivationBehavior.OneShot;
        _mqttDeactivationPayloadLabel.Visible = configuredInverse;
        _mqttDeactivationPayload.Visible = configuredInverse;
        _mqttDeactivationRetain.Enabled = !capturedRestore;
        _mqttVerifyDeactivation.Visible = configuredInverse;
        _mqttDeactivationExpectedState.Visible = configuredInverse &&
            _mqttVerifyDeactivation.Checked;
        _mqttDeactivationExpectedLabel.Visible = _mqttDeactivationExpectedState.Visible;
    }

    private void MqttResourceFieldChanged(object? sender, EventArgs e)
    {
        if (_loadingControls || SelectedMqtt is not MqttResourceConfig resource)
            return;

        resource.Enabled = _mqttEnabled.Checked;
        resource.Topic = _mqttTopic.Text;
        resource.Payload = _mqttPayload.Text;
        resource.Qos = _mqttQos.SelectedItem is MqttQualityOfService qos
            ? qos
            : MqttQualityOfService.AtLeastOnce;
        resource.Retain = _mqttRetain.Checked;
        resource.VerifyStateChange = _mqttVerify.Checked;
        resource.VerificationTopic = _mqttVerificationTopic.Text;
        resource.ExpectedState = _mqttExpectedState.Text;
        resource.VerificationTimeoutSeconds = ReadDisplayedNumber(_mqttVerificationTimeout);
        resource.DeactivationBehavior = _mqttDeactivationBehavior.SelectedItem is
            MqttDeactivationBehavior behavior
                ? behavior
                : MqttDeactivationBehavior.OneShot;
        resource.DeactivationTopic = _mqttDeactivationTopic.Text;
        resource.DeactivationPayload = _mqttDeactivationPayload.Text;
        resource.DeactivationQos = _mqttDeactivationQos.SelectedItem is
            MqttQualityOfService inverseQos
                ? inverseQos
                : MqttQualityOfService.AtLeastOnce;
        resource.DeactivationRetain = _mqttDeactivationRetain.Checked;
        resource.VerifyDeactivation = _mqttVerifyDeactivation.Checked;
        resource.DeactivationExpectedState = _mqttDeactivationExpectedState.Text;
        resource.Notifications.Target = [.. _mqttNotifications.SelectedTargets];
        _resourceList.Refresh();
        UpdateStatus();
    }

    private void AddMqttClicked(object? sender, EventArgs e)
    {
        if (SelectedProfile is not SupervisorProfileConfig profile)
            return;

        var resource = new MqttResourceConfig();
        profile.MqttResources.Add(resource);
        BindResourceList(profile, resource);
        LoadSelectedResource();
        _mqttTopic.Focus();
        UpdateStatus();
    }

    private void RemoveMqttClicked(object? sender, EventArgs e)
    {
        if (SelectedProfile is not SupervisorProfileConfig profile ||
            SelectedMqtt is not MqttResourceConfig selected ||
            !ConfirmRemoval("MQTT publish", MqttResource.GetDisplayName(selected)))
        {
            return;
        }

        profile.MqttResources.Remove(selected);
        ClearRemovedResourceDependencies(profile, selected.ResourceId);
        BindResourceList(profile);
        LoadSelectedResource();
        UpdateStatus();
    }

    private void TestMqttNotificationClicked(object? sender, EventArgs e) =>
        PublishTestNotification(_mqttNotifications.SelectedTargets, "MQTT publish");

    private async void TestMqttActionClicked(object? sender, EventArgs e)
    {
        if (_mqttActionTestPending || SelectedMqtt is not MqttResourceConfig selected)
            return;

        MqttResourceConfig resource = ConfigJson.Clone(selected);
        MqttIntegrationConfig integration = ConfigJson.Clone(_configuration.Integrations.Mqtt);
        _mqttActionTestPending = true;
        _testMqttActionButton.Enabled = false;
        _mqttStatus.Text = "Running MQTT preview...";

        try
        {
            ValidateMqttForTest(resource, integration);
            using var client = new MqttBrokerClient(integration);
            MqttActionTestResult result = await MqttActionTester.RunAsync(
                client,
                resource,
                CancellationToken.None
            );

            if (IsDisposed)
                return;

            string detail = result.Behavior switch
            {
                MqttDeactivationBehavior.OneShot =>
                    "The one-shot payload was published and left in place.",
                MqttDeactivationBehavior.PublishConfiguredPayload =>
                    "The activation payload was published for five seconds, then the configured inverse was published.",
                _ =>
                    "The activation payload was published for five seconds, then the exact retained pre-state was restored and verified."
            };
            _mqttStatus.Text = detail;
            MessageBox.Show(
                this,
                detail,
                "MQTT action test succeeded",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
        catch (Exception exception)
        {
            if (!IsDisposed)
            {
                _mqttStatus.Text = exception.Message;
                MessageBox.Show(
                    this,
                    exception.Message,
                    "MQTT action test failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
        }
        finally
        {
            _mqttActionTestPending = false;

            if (!_testMqttActionButton.IsDisposed)
                _testMqttActionButton.Enabled = SelectedMqtt is not null;
        }
    }

    private static void ValidateMqttForTest(
        MqttResourceConfig resource,
        MqttIntegrationConfig integration)
    {
        resource.Enabled = true;
        resource.StartupOrder = 0;
        resource.DependencyResourceId = "";
        var profile = new SupervisorProfileConfig
        {
            Name = "MQTT test",
            MonitorProcess = "AppSupervisor.MqttTest.Trigger.exe",
            MqttResources = [resource]
        };
        ConfigValidator.Validate([profile]);
        IntegrationConfigValidator.Validate(new IntegrationsConfig
        {
            Mqtt = integration
        }, [profile]);
    }

    private static TextBox CreateMqttPayloadEditor(string name) => new()
    {
        Name = name,
        Dock = DockStyle.Fill,
        Multiline = true,
        AcceptsReturn = true,
        ScrollBars = ScrollBars.Vertical,
        Height = 58
    };

    private static ComboBox CreateMqttSelector(string name) => new()
    {
        Name = name,
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
        FormattingEnabled = true
    };

    private static string FormatMqttQos(MqttQualityOfService qos) => qos switch
    {
        MqttQualityOfService.AtMostOnce => "0 — At most once",
        MqttQualityOfService.AtLeastOnce => "1 — At least once",
        MqttQualityOfService.ExactlyOnce => "2 — Exactly once",
        _ => qos.ToString()
    };

    private static string FormatMqttDeactivation(MqttDeactivationBehavior behavior) =>
        behavior switch
        {
            MqttDeactivationBehavior.OneShot => "One-shot (no inverse)",
            MqttDeactivationBehavior.PublishConfiguredPayload =>
                "Publish configured inverse payload",
            MqttDeactivationBehavior.RestoreRetainedState =>
                "Restore captured retained state",
            _ => behavior.ToString()
        };
}
