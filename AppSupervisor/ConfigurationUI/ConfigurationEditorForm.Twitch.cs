using AppSupervisor.Resources;
using AppSupervisor.Twitch;

namespace AppSupervisor.ConfigurationUI;

/// <summary>Provides Twitch broadcaster action editing and safe one-shot testing.</summary>
public sealed partial class ConfigurationEditorForm
{
    private readonly Panel _twitchEditorPanel = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _twitchEnabled = new() { Text = "Twitch action enabled", AutoSize = true };
    private readonly ComboBox _twitchAction = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
        DisplayMember = nameof(TwitchActionChoice.DisplayName)
    };
    private readonly TextBox _twitchMessage = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        Height = 80,
        MaxLength = 500,
        ScrollBars = ScrollBars.Vertical
    };
    private readonly ComboBox _twitchCommercialLength = new()
    {
        Dock = DockStyle.None,
        Width = 120,
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly CheckBox _twitchModeEnabled = new()
    {
        Text = "Enabled while profile is active",
        AutoSize = true
    };
    private readonly NumericUpDown _twitchFollowerMinutes = new()
    {
        Minimum = 0,
        Maximum = 129_600,
        Width = 120,
        ThousandsSeparator = true
    };
    private readonly NumericUpDown _twitchSlowSeconds = new()
    {
        Minimum = 3,
        Maximum = 120,
        Width = 120
    };
    private readonly Panel _twitchOptions = new()
    {
        Dock = DockStyle.Fill,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink
    };
    private readonly TableLayoutPanel _twitchMessageOptions = CreateEditorTable();
    private readonly TableLayoutPanel _twitchAdOptions = CreateEditorTable();
    private readonly TableLayoutPanel _twitchModeOptions = CreateEditorTable();
    private readonly NotificationTargetsControl _twitchNotifications = new();
    private Button _testTwitchActionButton = null!;
    private bool _twitchActionTestPending;

    private Control BuildTwitchEditor()
    {
        _twitchEditorPanel.Padding = new Padding(14);
        var scrolling = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        TableLayoutPanel layout = CreateEditorTable();
        _twitchAction.Items.AddRange(
        [
            new TwitchActionChoice(TwitchActionType.SendChatMessage, "Send chat message"),
            new TwitchActionChoice(TwitchActionType.RunCommercial, "Play advertisement"),
            new TwitchActionChoice(TwitchActionType.EmoteOnly, "Emote-only chat"),
            new TwitchActionChoice(TwitchActionType.FollowersOnly, "Followers-only chat"),
            new TwitchActionChoice(TwitchActionType.SlowMode, "Slow chat"),
            new TwitchActionChoice(TwitchActionType.SubscribersOnly, "Subscribers-only chat")
        ]);
        _twitchCommercialLength.Items.AddRange([30, 60, 90, 120, 150, 180]);
        AddEditorRow(layout, "", _twitchEnabled);
        AddEditorRow(layout, "Twitch action", _twitchAction);
        BuildTwitchOptions();
        AddSpanningEditorRow(layout, _twitchOptions);
        _testTwitchActionButton = CreateButton("Test action", TestTwitchActionClicked);
        AddEditorRow(layout, "Action", _testTwitchActionButton);
        AddEditorRow(layout, "Notifications", BuildNotificationTestPanel(
            _twitchNotifications,
            TestTwitchNotificationClicked
        ));
        AddEditorRow(layout, "", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Messages and advertisements run once at activation. Chat modes capture their original Twitch values, remain applied while the monitored app is running, and restore those exact values after it closes."
        });
        scrolling.Controls.Add(layout);
        _twitchEditorPanel.Controls.Add(scrolling);

        _twitchEnabled.CheckedChanged += TwitchResourceFieldChanged;
        _twitchAction.SelectedIndexChanged += TwitchActionChanged;
        _twitchMessage.TextChanged += TwitchResourceFieldChanged;
        _twitchCommercialLength.SelectedIndexChanged += TwitchResourceFieldChanged;
        _twitchModeEnabled.CheckedChanged += TwitchResourceFieldChanged;
        _twitchFollowerMinutes.ValueChanged += TwitchResourceFieldChanged;
        _twitchFollowerMinutes.TextChanged += TwitchResourceFieldChanged;
        _twitchSlowSeconds.ValueChanged += TwitchResourceFieldChanged;
        _twitchSlowSeconds.TextChanged += TwitchResourceFieldChanged;
        _twitchNotifications.TargetsChanged += TwitchResourceFieldChanged;
        return _twitchEditorPanel;
    }

    private void BuildTwitchOptions()
    {
        _twitchMessageOptions.Dock = DockStyle.Top;
        AddEditorRow(_twitchMessageOptions, "Message", _twitchMessage, alignTop: true);
        AddEditorRow(_twitchMessageOptions, "", new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = "Maximum 500 characters. Twitch emote names are supported and are case-sensitive."
        });

        _twitchAdOptions.Dock = DockStyle.Top;
        _twitchAdOptions.Padding = new Padding(0, 0, 0, 3);
        AddEditorRow(_twitchAdOptions, "Ad length", BuildSecondsEditor(_twitchCommercialLength));

        _twitchModeOptions.Dock = DockStyle.Top;
        AddEditorRow(_twitchModeOptions, "", _twitchModeEnabled);
        AddEditorRow(_twitchModeOptions, "Minimum follow", BuildNumberUnitEditor(_twitchFollowerMinutes, "minutes"));
        AddEditorRow(_twitchModeOptions, "Message interval", BuildNumberUnitEditor(_twitchSlowSeconds, "seconds"));

        _twitchOptions.Controls.Add(_twitchMessageOptions);
        _twitchOptions.Controls.Add(_twitchAdOptions);
        _twitchOptions.Controls.Add(_twitchModeOptions);
    }

    private static Control BuildSecondsEditor(ComboBox selector)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = new Padding(0, 0, 0, 3)
        };
        selector.Margin = Padding.Empty;
        panel.Controls.Add(selector);
        panel.Controls.Add(new Label { Text = "seconds", AutoSize = true, Margin = new Padding(4, 7, 0, 0) });
        return panel;
    }

    private static Control BuildNumberUnitEditor(NumericUpDown numeric, string unit)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        panel.Controls.Add(numeric);
        panel.Controls.Add(new Label { Text = unit, AutoSize = true, Margin = new Padding(4, 7, 0, 0) });
        return panel;
    }

    private void LoadSelectedTwitch()
    {
        TwitchResourceConfig? resource = SelectedTwitch;
        _loadingControls = true;
        try
        {
            _twitchEditorPanel.Enabled = resource is not null;
            _testTwitchActionButton.Enabled = resource is not null && !_twitchActionTestPending;
            _twitchEnabled.Checked = resource?.Enabled ?? false;
            _twitchAction.SelectedItem = _twitchAction.Items.Cast<TwitchActionChoice>()
                .FirstOrDefault(choice => choice.Value == resource?.Action);
            _twitchMessage.Text = resource?.Message ?? "";
            _twitchCommercialLength.SelectedItem = resource?.CommercialLengthSeconds ?? 30;
            _twitchModeEnabled.Checked = resource?.ModeEnabled ?? true;
            _twitchFollowerMinutes.Value = Math.Clamp(resource?.FollowerDurationMinutes ?? 0, 0, 129_600);
            _twitchSlowSeconds.Value = Math.Clamp(resource?.SlowModeWaitSeconds ?? 30, 3, 120);
            _twitchNotifications.LoadTargets(resource?.Notifications.Target ?? []);
            UpdateTwitchOptionVisibility();
        }
        finally
        {
            _loadingControls = false;
        }
    }

    private void TwitchActionChanged(object? sender, EventArgs e)
    {
        if (_loadingControls || SelectedTwitch is not TwitchResourceConfig resource)
            return;
        resource.Action = (_twitchAction.SelectedItem as TwitchActionChoice)?.Value ?? resource.Action;
        UpdateTwitchOptionVisibility();
        TwitchResourceFieldChanged(sender, e);
    }

    private void UpdateTwitchOptionVisibility()
    {
        TwitchActionType action = (_twitchAction.SelectedItem as TwitchActionChoice)?.Value ?? TwitchActionType.SendChatMessage;
        _twitchMessageOptions.Visible = action == TwitchActionType.SendChatMessage;
        _twitchAdOptions.Visible = action == TwitchActionType.RunCommercial;
        _twitchModeOptions.Visible = action is TwitchActionType.EmoteOnly or TwitchActionType.FollowersOnly or TwitchActionType.SlowMode or TwitchActionType.SubscribersOnly;
        SetTwitchModeRowVisible(1, action == TwitchActionType.FollowersOnly);
        SetTwitchModeRowVisible(2, action == TwitchActionType.SlowMode);
        if (_twitchMessageOptions.Visible) _twitchMessageOptions.BringToFront();
        else if (_twitchAdOptions.Visible) _twitchAdOptions.BringToFront();
        else _twitchModeOptions.BringToFront();
        _twitchModeOptions.PerformLayout();
        _twitchOptions.PerformLayout();
    }

    /// <summary>Shows or collapses both the label and editor belonging to one mode-specific row.</summary>
    private void SetTwitchModeRowVisible(int row, bool visible)
    {
        foreach (Control control in _twitchModeOptions.Controls)
        {
            if (_twitchModeOptions.GetRow(control) == row)
                control.Visible = visible;
        }
    }

    private void TwitchResourceFieldChanged(object? sender, EventArgs e)
    {
        if (_loadingControls || SelectedTwitch is not TwitchResourceConfig resource)
            return;
        resource.Enabled = _twitchEnabled.Checked;
        resource.Action = (_twitchAction.SelectedItem as TwitchActionChoice)?.Value ?? resource.Action;
        resource.Message = _twitchMessage.Text;
        resource.CommercialLengthSeconds = _twitchCommercialLength.SelectedItem is int length ? length : 30;
        resource.ModeEnabled = _twitchModeEnabled.Checked;
        resource.FollowerDurationMinutes = ReadDisplayedNumber(_twitchFollowerMinutes);
        resource.SlowModeWaitSeconds = ReadDisplayedNumber(_twitchSlowSeconds);
        resource.Notifications.Target = [.. _twitchNotifications.SelectedTargets];
        _resourceList.Refresh();
        UpdateStatus();
    }

    private void AddTwitchClicked(object? sender, EventArgs e)
    {
        if (SelectedProfile is not SupervisorProfileConfig profile)
            return;
        var resource = new TwitchResourceConfig();
        profile.TwitchResources.Add(resource);
        BindResourceList(profile, resource);
        LoadSelectedResource();
        _twitchAction.Focus();
        UpdateStatus();
    }

    private void RemoveTwitchClicked(object? sender, EventArgs e)
    {
        if (SelectedProfile is not SupervisorProfileConfig profile ||
            SelectedTwitch is not TwitchResourceConfig selected ||
            !ConfirmRemoval("Twitch action", TwitchResource.GetDisplayName(selected)))
            return;
        profile.TwitchResources.Remove(selected);
        ClearRemovedResourceDependencies(profile, selected.ResourceId);
        BindResourceList(profile);
        LoadSelectedResource();
        UpdateStatus();
    }

    private async void TestTwitchActionClicked(object? sender, EventArgs e)
    {
        if (_twitchActionTestPending || SelectedTwitch is not TwitchResourceConfig resource)
            return;
        _twitchActionTestPending = true;
        _testTwitchActionButton.Enabled = false;
        try
        {
            using var client = new TwitchApiClient(_configuration.Integrations.Twitch);
            if (resource.Action == TwitchActionType.SendChatMessage)
                await client.SendChatMessageAsync(resource.Message, CancellationToken.None);
            else if (resource.Action == TwitchActionType.RunCommercial)
                await client.RunCommercialAsync(resource.CommercialLengthSeconds, CancellationToken.None);
            else
            {
                TwitchChatSettings original = await client.GetChatSettingsAsync(CancellationToken.None);
                await client.UpdateChatSettingsAsync(TwitchResource.CreateActivationUpdate(resource), CancellationToken.None);
                await client.UpdateChatSettingsAsync(TwitchResource.CreateRestoreUpdate(resource.Action, original), CancellationToken.None);
            }
            MessageBox.Show(this, "The Twitch action succeeded." +
                (resource.Action is TwitchActionType.SendChatMessage or TwitchActionType.RunCommercial
                    ? ""
                    : " The original chat setting was restored."),
                "Twitch action test succeeded", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Twitch action test failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _twitchActionTestPending = false;
            if (!_testTwitchActionButton.IsDisposed)
                _testTwitchActionButton.Enabled = SelectedTwitch is not null;
        }
    }

    private void TestTwitchNotificationClicked(object? sender, EventArgs e) =>
        PublishTestNotification(_twitchNotifications.SelectedTargets, "Twitch action");

    private sealed record TwitchActionChoice(TwitchActionType Value, string DisplayName);
}
