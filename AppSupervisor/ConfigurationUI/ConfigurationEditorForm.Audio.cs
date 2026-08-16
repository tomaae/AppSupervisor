using AppSupervisor.Resources;
using AppSupervisor.WindowsAudio;

namespace AppSupervisor.ConfigurationUI;

/// <summary>Provides Windows audio endpoint volume and mute action editing.</summary>
public sealed partial class ConfigurationEditorForm
{
    private readonly Panel _audioInterfaceEditorPanel = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _audioInterfaceEnabled = new()
    {
        Text = "Audio interface action enabled",
        AutoSize = true
    };
    private readonly ComboBox _audioInterfaceSelector = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
        DisplayMember = nameof(AudioEndpointSnapshot.DisplayName)
    };
    private readonly NumericUpDown _audioVolume = new()
    {
        Minimum = 0,
        Maximum = 100,
        Width = 90
    };
    private readonly ComboBox _audioMuteState = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
        DisplayMember = nameof(AudioMuteChoice.DisplayName)
    };
    private readonly CheckBox _audioRestoreOnDeactivate = new()
    {
        Text = "Restore original volume and mute when monitored app closes",
        AutoSize = true
    };
    private readonly NotificationTargetsControl _audioNotifications = new();
    private readonly Label _audioDiscoveryStatus = new()
    {
        AutoSize = true,
        ForeColor = SystemColors.GrayText
    };
    private Button _refreshAudioInterfacesButton = null!;
    private Button _testAudioActionButton = null!;
    private bool _audioActionTestPending;

    private Control BuildAudioInterfaceEditor()
    {
        _audioInterfaceEditorPanel.Padding = new Padding(14);
        var scrolling = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        TableLayoutPanel layout = CreateEditorTable();
        _audioMuteState.Items.AddRange(
        [
            new AudioMuteChoice(false, "Unmuted"),
            new AudioMuteChoice(true, "Muted")
        ]);

        AddEditorRow(layout, "", _audioInterfaceEnabled);
        AddEditorRow(layout, "Interface", BuildAudioInterfaceSelector());
        AddEditorRow(layout, "Volume", BuildPercentEditor(_audioVolume));
        AddEditorRow(layout, "Mute state", _audioMuteState);
        AddEditorRow(layout, "On close", _audioRestoreOnDeactivate);
        _testAudioActionButton = CreateButton("Test for 5 seconds", TestAudioActionClicked);
        AddEditorRow(layout, "Action", _testAudioActionButton);
        AddEditorRow(layout, "Notifications", BuildNotificationTestPanel(
            _audioNotifications,
            TestAudioNotificationClicked
        ));
        AddEditorRow(layout, "", _audioDiscoveryStatus);
        AddEditorRow(layout, "", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            ForeColor = SystemColors.GrayText,
            Text = "AppSupervisor remembers the selected endpoint's device and container identity, so it can find the same interface if Windows changes the endpoint ID. Name-only recovery is used only when it identifies one unambiguous active interface."
        });
        scrolling.Controls.Add(layout);
        _audioInterfaceEditorPanel.Controls.Add(scrolling);

        _audioInterfaceEnabled.CheckedChanged += AudioInterfaceFieldChanged;
        _audioInterfaceSelector.SelectedIndexChanged += AudioInterfaceSelectionChanged;
        _audioVolume.ValueChanged += AudioInterfaceFieldChanged;
        _audioMuteState.SelectedIndexChanged += AudioInterfaceFieldChanged;
        _audioRestoreOnDeactivate.CheckedChanged += AudioInterfaceFieldChanged;
        _audioNotifications.TargetsChanged += AudioInterfaceFieldChanged;
        return _audioInterfaceEditorPanel;
    }

    private Control BuildAudioInterfaceSelector()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Margin = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _audioInterfaceSelector.Margin = Padding.Empty;
        _refreshAudioInterfacesButton = CreateButton("Refresh", RefreshAudioInterfacesClicked);
        panel.Controls.Add(_audioInterfaceSelector, 0, 0);
        panel.Controls.Add(_refreshAudioInterfacesButton, 1, 0);
        return panel;
    }

    private static Control BuildPercentEditor(NumericUpDown numeric)
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
            Text = "%",
            AutoSize = true,
            Margin = new Padding(4, 7, 0, 0)
        });
        return panel;
    }

    private async Task LoadSelectedAudioInterfaceAsync()
    {
        AudioInterfaceResourceConfig? resource = SelectedAudioInterface;
        _loadingControls = true;

        try
        {
            _audioInterfaceEditorPanel.Enabled = resource is not null;
            _testAudioActionButton.Enabled = resource is not null && !_audioActionTestPending;
            _audioInterfaceEnabled.Checked = resource?.Enabled ?? false;
            _audioVolume.Value = Math.Clamp(resource?.VolumePercent ?? 100, 0, 100);
            _audioMuteState.SelectedItem = _audioMuteState.Items
                .Cast<AudioMuteChoice>()
                .First(choice => choice.Muted == (resource?.Muted ?? false));
            _audioRestoreOnDeactivate.Checked = resource?.RestoreOnDeactivate ?? true;
            _audioNotifications.LoadTargets(resource?.Notifications.Target ?? []);
            _audioInterfaceSelector.Items.Clear();
            _audioDiscoveryStatus.Text = resource is null
                ? ""
                : "Loading active Windows audio interfaces...";
        }
        finally
        {
            _loadingControls = false;
        }

        if (resource is null)
            return;

        try
        {
            IReadOnlyList<AudioEndpointSnapshot> endpoints = await _audioEndpointLoader(
                CancellationToken.None
            );

            if (IsDisposed || !ReferenceEquals(resource, SelectedAudioInterface))
                return;

            BindAudioEndpoints(endpoints, resource);
            int physicalCount = endpoints.Count(endpoint => !endpoint.FollowsDefault);
            _audioDiscoveryStatus.Text =
                $"{physicalCount} active Windows audio interface(s) found; default output/input choices follow Windows settings.";
        }
        catch (Exception ex)
        {
            if (!IsDisposed && ReferenceEquals(resource, SelectedAudioInterface))
                _audioDiscoveryStatus.Text = ex.Message;
        }
    }

    private void BindAudioEndpoints(
        IReadOnlyList<AudioEndpointSnapshot> endpoints,
        AudioInterfaceResourceConfig resource)
    {
        _loadingControls = true;

        try
        {
            _audioInterfaceSelector.Items.Clear();

            foreach (AudioEndpointSnapshot endpoint in endpoints)
                _audioInterfaceSelector.Items.Add(endpoint);

            AudioEndpointSnapshot? selected = resource.UseDefaultDevice
                ? endpoints.FirstOrDefault(endpoint =>
                    endpoint.FollowsDefault && endpoint.Direction == resource.Direction)
                : endpoints.FirstOrDefault(endpoint =>
                    !endpoint.FollowsDefault && string.Equals(
                        endpoint.EndpointId,
                        resource.EndpointId,
                        StringComparison.OrdinalIgnoreCase
                    ));

            if (selected is null && !resource.UseDefaultDevice)
            {
                try
                {
                    selected = WindowsAudioEndpointResolver.Resolve(resource, endpoints);
                }
                catch
                {
                    // Preserve the configured identity and show no selection when it is unavailable or ambiguous.
                }
            }

            _audioInterfaceSelector.SelectedItem = selected;
        }
        finally
        {
            _loadingControls = false;
        }
    }

    private void AudioInterfaceSelectionChanged(object? sender, EventArgs e)
    {
        if (_loadingControls ||
            SelectedAudioInterface is not AudioInterfaceResourceConfig resource ||
            _audioInterfaceSelector.SelectedItem is not AudioEndpointSnapshot endpoint)
        {
            return;
        }

        endpoint.CopyIdentityTo(resource);
        AudioInterfaceFieldChanged(sender, e);
    }

    private void AudioInterfaceFieldChanged(object? sender, EventArgs e)
    {
        if (_loadingControls || SelectedAudioInterface is not AudioInterfaceResourceConfig resource)
            return;

        resource.Enabled = _audioInterfaceEnabled.Checked;
        resource.VolumePercent = Decimal.ToInt32(_audioVolume.Value);
        resource.Muted = (_audioMuteState.SelectedItem as AudioMuteChoice)?.Muted ?? resource.Muted;
        resource.RestoreOnDeactivate = _audioRestoreOnDeactivate.Checked;
        resource.Notifications.Target = [.. _audioNotifications.SelectedTargets];
        _resourceList.Refresh();
        UpdateStatus();
    }

    private async void AddAudioInterfaceClicked(object? sender, EventArgs e)
    {
        if (SelectedProfile is not SupervisorProfileConfig profile)
            return;

        try
        {
            IReadOnlyList<AudioEndpointSnapshot> endpoints = await _audioEndpointLoader(
                CancellationToken.None
            );
            AudioEndpointSnapshot endpoint = endpoints.FirstOrDefault() ??
                throw new InvalidOperationException("Windows did not expose any active audio interfaces.");
            var resource = new AudioInterfaceResourceConfig();
            endpoint.CopyIdentityTo(resource);
            profile.AudioInterfaces.Add(resource);
            BindResourceList(profile, resource);
            LoadSelectedResource();
            _audioInterfaceSelector.Focus();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Cannot add Windows audio interface",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
    }

    private void RemoveAudioInterfaceClicked(object? sender, EventArgs e)
    {
        if (SelectedProfile is not SupervisorProfileConfig profile ||
            SelectedAudioInterface is not AudioInterfaceResourceConfig selected ||
            !ConfirmRemoval("Windows audio interface", AudioInterfaceResource.GetDisplayName(selected)))
        {
            return;
        }

        profile.AudioInterfaces.Remove(selected);
        ClearRemovedResourceDependencies(profile, selected.ResourceId);
        BindResourceList(profile);
        LoadSelectedResource();
        UpdateStatus();
    }

    private async void RefreshAudioInterfacesClicked(object? sender, EventArgs e)
    {
        _refreshAudioInterfacesButton.Enabled = false;

        try
        {
            await LoadSelectedAudioInterfaceAsync();
        }
        finally
        {
            if (!_refreshAudioInterfacesButton.IsDisposed)
                _refreshAudioInterfacesButton.Enabled = true;
        }
    }

    private async void TestAudioActionClicked(object? sender, EventArgs e)
    {
        if (_audioActionTestPending ||
            SelectedAudioInterface is not AudioInterfaceResourceConfig resource)
        {
            return;
        }

        var testConfiguration = new AudioInterfaceResourceConfig
        {
            EndpointId = resource.EndpointId,
            DeviceInstanceId = resource.DeviceInstanceId,
            ContainerId = resource.ContainerId,
            FriendlyName = resource.FriendlyName,
            InterfaceName = resource.InterfaceName,
            Direction = resource.Direction,
            UseDefaultDevice = resource.UseDefaultDevice,
            VolumePercent = resource.VolumePercent,
            Muted = resource.Muted
        };
        _audioActionTestPending = true;
        _testAudioActionButton.Enabled = false;
        _audioDiscoveryStatus.Text =
            "Applying the requested volume and mute state for five seconds...";

        try
        {
            var controller = new WindowsAudioController();
            AudioActionTestResult result = await WindowsAudioActionTester.RunAsync(
                controller,
                testConfiguration,
                TimeSpan.FromSeconds(5),
                CancellationToken.None
            );

            if (IsDisposed)
                return;

            if (ReferenceEquals(resource, SelectedAudioInterface))
            {
                _audioDiscoveryStatus.Text =
                    "Test succeeded; the original volume and mute state were restored.";
            }

            MessageBox.Show(
                this,
                $"The requested state was applied to {result.EndpointDisplayName} for five seconds, then the original volume and mute state were restored.",
                "Windows audio test succeeded",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
        catch (Exception ex)
        {
            if (!IsDisposed && ReferenceEquals(resource, SelectedAudioInterface))
                _audioDiscoveryStatus.Text = ex.Message;

            if (!IsDisposed)
            {
                MessageBox.Show(
                    this,
                    ex.Message,
                    "Windows audio test failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
        }
        finally
        {
            _audioActionTestPending = false;

            if (!_testAudioActionButton.IsDisposed)
                _testAudioActionButton.Enabled = SelectedAudioInterface is not null;
        }
    }

    private void TestAudioNotificationClicked(object? sender, EventArgs e) =>
        PublishTestNotification(_audioNotifications.SelectedTargets, "Windows audio interface");

    private static void DrawAudioIcon(Graphics graphics, Pen pen, Rectangle bounds)
    {
        float middleY = bounds.Top + bounds.Height / 2f;
        var speaker = new PointF[]
        {
            new(bounds.Left + bounds.Width * 0.12f, middleY - bounds.Height * 0.15f),
            new(bounds.Left + bounds.Width * 0.34f, middleY - bounds.Height * 0.15f),
            new(bounds.Left + bounds.Width * 0.53f, bounds.Top + bounds.Height * 0.18f),
            new(bounds.Left + bounds.Width * 0.53f, bounds.Bottom - bounds.Height * 0.18f),
            new(bounds.Left + bounds.Width * 0.34f, middleY + bounds.Height * 0.15f),
            new(bounds.Left + bounds.Width * 0.12f, middleY + bounds.Height * 0.15f)
        };
        graphics.DrawPolygon(pen, speaker);
        graphics.DrawArc(
            pen,
            bounds.Left + bounds.Width * 0.39f,
            bounds.Top + bounds.Height * 0.25f,
            bounds.Width * 0.42f,
            bounds.Height * 0.5f,
            -55,
            110
        );
    }

    private sealed record AudioMuteChoice(bool Muted, string DisplayName);
}
