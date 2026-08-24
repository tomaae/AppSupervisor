using AppSupervisor.Resources;
using AppSupervisor.StreamDeck;

namespace AppSupervisor.ConfigurationUI;

/// <summary>Provides Stream Deck MCP action discovery, editing, and testing.</summary>
public sealed partial class ConfigurationEditorForm
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<StreamDeckMcpAction>>>
        _streamDeckActionLoader;
    private readonly Func<StreamDeckResourceConfig, CancellationToken, Task>
        _streamDeckActionExecutor;
    private readonly Panel _streamDeckEditorPanel = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _streamDeckEnabled = new()
    {
        Text = "Stream Deck action enabled",
        AutoSize = true
    };
    private readonly ComboBox _streamDeckAction = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
        DisplayMember = nameof(StreamDeckMcpAction.DisplayName)
    };
    private readonly NotificationTargetsControl _streamDeckNotifications = new();
    private readonly Label _streamDeckStatus = new()
    {
        AutoSize = true,
        MaximumSize = new Size(680, 0),
        ForeColor = SystemColors.GrayText
    };
    private Button _refreshStreamDeckButton = null!;
    private Button _testStreamDeckButton = null!;
    private bool _streamDeckDiscoveryPending;
    private bool _streamDeckActionTestPending;

    private Control BuildStreamDeckEditor()
    {
        _streamDeckEditorPanel.Padding = new Padding(14);
        var scrolling = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        TableLayoutPanel layout = CreateEditorTable();
        AddEditorRow(layout, "", _streamDeckEnabled);
        AddEditorRow(layout, "Stream Deck action", BuildStreamDeckActionSelector());
        _testStreamDeckButton = CreateButton("Test action", TestStreamDeckActionClicked);
        AddEditorRow(layout, "Action", _testStreamDeckButton);
        AddEditorRow(layout, "Notifications", BuildNotificationTestPanel(
            _streamDeckNotifications,
            TestStreamDeckNotificationClicked
        ));
        AddEditorRow(layout, "", _streamDeckStatus);
        AddEditorRow(layout, "", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Actions come from Stream Deck's MCP Actions profile and run once when this profile activates. AppSupervisor does not simulate a key press and does not reverse the action when the monitored app closes."
        });
        scrolling.Controls.Add(layout);
        _streamDeckEditorPanel.Controls.Add(scrolling);

        _streamDeckEnabled.CheckedChanged += StreamDeckResourceFieldChanged;
        _streamDeckAction.SelectedIndexChanged += StreamDeckResourceFieldChanged;
        _streamDeckNotifications.TargetsChanged += StreamDeckResourceFieldChanged;
        return _streamDeckEditorPanel;
    }

    private Control BuildStreamDeckActionSelector()
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
        _streamDeckAction.Margin = Padding.Empty;
        _refreshStreamDeckButton = CreateButton("Refresh", RefreshStreamDeckActionsClicked);
        panel.Controls.Add(_streamDeckAction, 0, 0);
        panel.Controls.Add(_refreshStreamDeckButton, 1, 0);
        return panel;
    }

    private async Task LoadSelectedStreamDeckAsync()
    {
        StreamDeckResourceConfig? resource = SelectedStreamDeck;
        _loadingControls = true;

        try
        {
            _streamDeckEditorPanel.Enabled = resource is not null;
            _streamDeckEnabled.Checked = resource?.Enabled ?? false;
            _streamDeckNotifications.LoadTargets(resource?.Notifications.Target ?? []);
            _streamDeckAction.Items.Clear();
            UpdateStreamDeckButtons();
            _streamDeckStatus.Text = resource is null ? "" : "Loading Stream Deck actions...";
        }
        finally
        {
            _loadingControls = false;
        }

        if (resource is null || _streamDeckDiscoveryPending)
            return;

        _streamDeckDiscoveryPending = true;
        UpdateStreamDeckButtons();

        try
        {
            IReadOnlyList<StreamDeckMcpAction> actions = await _streamDeckActionLoader(
                CancellationToken.None
            );

            if (!ReferenceEquals(resource, SelectedStreamDeck) || IsDisposed)
                return;

            BindStreamDeckActions(actions, resource);
            _streamDeckStatus.Text = actions.Count == 0
                ? "No actions are available. Add actions to Stream Deck's MCP Actions profile."
                : $"{actions.Count} Stream Deck action{(actions.Count == 1 ? "" : "s")} available.";
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(resource, SelectedStreamDeck) || IsDisposed)
                return;

            BindStreamDeckActions([], resource);
            _streamDeckStatus.Text = ex.Message;
        }
        finally
        {
            _streamDeckDiscoveryPending = false;

            if (!_streamDeckEditorPanel.IsDisposed)
                UpdateStreamDeckButtons();
        }
    }

    private void BindStreamDeckActions(
        IReadOnlyList<StreamDeckMcpAction> actions,
        StreamDeckResourceConfig resource)
    {
        _loadingControls = true;

        try
        {
            _streamDeckAction.Items.Clear();
            foreach (StreamDeckMcpAction action in actions)
                _streamDeckAction.Items.Add(action);

            StreamDeckMcpAction? selected = actions.FirstOrDefault(action =>
                string.Equals(action.ActionId, resource.ActionId, StringComparison.Ordinal));

            if (selected is null && !string.IsNullOrWhiteSpace(resource.ActionId))
            {
                selected = new StreamDeckMcpAction(
                    resource.ActionId,
                    string.IsNullOrWhiteSpace(resource.ActionName)
                        ? resource.ActionId
                        : resource.ActionName,
                    "Previously configured action"
                );
                _streamDeckAction.Items.Add(selected);
            }

            _streamDeckAction.SelectedItem = selected ??
                (_streamDeckAction.Items.Count > 0 ? _streamDeckAction.Items[0] : null);
        }
        finally
        {
            _loadingControls = false;
        }

        if (string.IsNullOrWhiteSpace(resource.ActionId) &&
            _streamDeckAction.SelectedItem is StreamDeckMcpAction first)
        {
            resource.ActionId = first.ActionId;
            resource.ActionName = first.DisplayName;
            _resourceList.Refresh();
            UpdateStatus();
        }
    }

    private void StreamDeckResourceFieldChanged(object? sender, EventArgs e)
    {
        if (_loadingControls || SelectedStreamDeck is not StreamDeckResourceConfig resource)
            return;

        resource.Enabled = _streamDeckEnabled.Checked;
        if (_streamDeckAction.SelectedItem is StreamDeckMcpAction action)
        {
            resource.ActionId = action.ActionId;
            resource.ActionName = action.DisplayName;
        }

        resource.Notifications.Target = [.. _streamDeckNotifications.SelectedTargets];
        _resourceList.Refresh();
        UpdateStreamDeckButtons();
        UpdateStatus();
    }

    private void AddStreamDeckClicked(object? sender, EventArgs e)
    {
        if (SelectedProfile is not SupervisorProfileConfig profile)
            return;

        var resource = new StreamDeckResourceConfig { Enabled = false };
        profile.StreamDeckResources.Add(resource);
        BindResourceList(profile, resource);
        LoadSelectedResource();
        _streamDeckAction.Focus();
        UpdateStatus();
    }

    private void RemoveStreamDeckClicked(object? sender, EventArgs e)
    {
        if (SelectedProfile is not SupervisorProfileConfig profile ||
            SelectedStreamDeck is not StreamDeckResourceConfig selected ||
            !ConfirmRemoval("Stream Deck action", StreamDeckResource.GetDisplayName(selected)))
        {
            return;
        }

        profile.StreamDeckResources.Remove(selected);
        ClearRemovedResourceDependencies(profile, selected.ResourceId);
        BindResourceList(profile);
        LoadSelectedResource();
        UpdateStatus();
    }

    private async void RefreshStreamDeckActionsClicked(object? sender, EventArgs e)
        => await LoadSelectedStreamDeckAsync();

    private async void TestStreamDeckActionClicked(object? sender, EventArgs e)
    {
        if (_streamDeckActionTestPending ||
            SelectedStreamDeck is not StreamDeckResourceConfig resource ||
            string.IsNullOrWhiteSpace(resource.ActionId))
        {
            return;
        }

        _streamDeckActionTestPending = true;
        UpdateStreamDeckButtons();
        _streamDeckStatus.Text = "Running Stream Deck action...";

        try
        {
            await _streamDeckActionExecutor(resource, CancellationToken.None);

            if (!IsDisposed && ReferenceEquals(resource, SelectedStreamDeck))
                _streamDeckStatus.Text = "Test action succeeded.";
        }
        catch (Exception ex)
        {
            if (!IsDisposed && ReferenceEquals(resource, SelectedStreamDeck))
                _streamDeckStatus.Text = ex.Message;

            if (!IsDisposed)
            {
                MessageBox.Show(
                    this,
                    ex.Message,
                    "Stream Deck action test failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
        }
        finally
        {
            _streamDeckActionTestPending = false;

            if (!_streamDeckEditorPanel.IsDisposed)
                UpdateStreamDeckButtons();
        }
    }

    private void UpdateStreamDeckButtons()
    {
        bool selected = SelectedStreamDeck is not null;
        _refreshStreamDeckButton.Enabled = selected && !_streamDeckDiscoveryPending;
        _testStreamDeckButton.Enabled = selected && !_streamDeckDiscoveryPending &&
            !_streamDeckActionTestPending && _streamDeckAction.SelectedItem is not null;
    }

    private void TestStreamDeckNotificationClicked(object? sender, EventArgs e)
        => PublishTestNotification(_streamDeckNotifications.SelectedTargets, "Stream Deck action");
}
