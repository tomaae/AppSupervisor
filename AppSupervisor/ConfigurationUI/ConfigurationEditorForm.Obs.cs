using AppSupervisor.Configuration;
using AppSupervisor.Obs;
using AppSupervisor.Resources;

namespace AppSupervisor.ConfigurationUI;

/// <summary>Provides deterministic, non-reversing OBS profile-action editing.</summary>
public sealed partial class ConfigurationEditorForm
{
    private readonly Panel _obsEditorPanel = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _obsEnabled = new()
    {
        Text = "OBS action enabled",
        AutoSize = true
    };
    private readonly ComboBox _obsAction = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
        DisplayMember = nameof(ObsActionChoice.DisplayName)
    };
    private readonly ComboBox _obsScene = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly ComboBox _obsInput = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly ComboBox _obsSource = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly CheckBox _obsMuted = new()
    {
        Text = "Muted",
        AutoSize = true
    };
    private readonly CheckBox _obsVisible = new()
    {
        Text = "Visible",
        AutoSize = true
    };
    private readonly Panel _obsActionOptions = new()
    {
        Dock = DockStyle.Fill,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink
    };
    private readonly TableLayoutPanel _obsAudioOptions = CreateEditorTable();
    private readonly TableLayoutPanel _obsVisibilityOptions = CreateEditorTable();
    private readonly NotificationTargetsControl _obsNotifications = new();
    private readonly Label _obsDiscoveryStatus = new()
    {
        AutoSize = true,
        ForeColor = SystemColors.GrayText
    };
    private Button _refreshObsButton = null!;
    private Button _testObsActionButton = null!;
    private bool _obsActionTestPending;

    private Control BuildObsEditor()
    {
        _obsEditorPanel.Padding = new Padding(14);
        var scrolling = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        TableLayoutPanel layout = CreateEditorTable();
        _obsAction.Items.AddRange(
        [
            new ObsActionChoice(ObsActionType.SwitchScene, "Change scene"),
            new ObsActionChoice(ObsActionType.SetInputMute, "Audio: Toggle mute"),
            new ObsActionChoice(ObsActionType.SetSourceVisibility, "Source visibility")
        ]);

        AddEditorRow(layout, "", _obsEnabled);
        AddEditorRow(layout, "OBS action", BuildObsActionSelector());
        AddEditorRow(layout, "Scene", _obsScene);
        BuildObsActionOptions();
        AddSpanningEditorRow(layout, _obsActionOptions);
        _testObsActionButton = CreateButton("Test action", TestObsActionClicked);
        AddEditorRow(layout, "Action", _testObsActionButton);
        AddEditorRow(layout, "Notifications", BuildNotificationTestPanel(
            _obsNotifications,
            TestObsNotificationClicked
        ));
        AddEditorRow(layout, "", _obsDiscoveryStatus);
        AddEditorRow(layout, "", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            ForeColor = SystemColors.GrayText,
            Text = "The configured action runs once when the profile activates. AppSupervisor never restores or toggles the OBS state when the profile deactivates. Test action also leaves the requested state in place."
        });
        scrolling.Controls.Add(layout);
        _obsEditorPanel.Controls.Add(scrolling);

        _obsEnabled.CheckedChanged += ObsResourceFieldChanged;
        _obsAction.SelectedIndexChanged += ObsActionChanged;
        _obsScene.SelectedIndexChanged += ObsSceneChanged;
        _obsInput.SelectedIndexChanged += ObsResourceFieldChanged;
        _obsSource.SelectedIndexChanged += ObsResourceFieldChanged;
        _obsMuted.CheckedChanged += ObsResourceFieldChanged;
        _obsVisible.CheckedChanged += ObsResourceFieldChanged;
        _obsNotifications.TargetsChanged += ObsResourceFieldChanged;
        return _obsEditorPanel;
    }

    private void BuildObsActionOptions()
    {
        _obsAudioOptions.Dock = DockStyle.Top;
        AddEditorRow(_obsAudioOptions, "Audio source", _obsInput);
        AddEditorRow(_obsAudioOptions, "", _obsMuted);

        _obsVisibilityOptions.Dock = DockStyle.Top;
        AddEditorRow(_obsVisibilityOptions, "Source", _obsSource);
        AddEditorRow(_obsVisibilityOptions, "", _obsVisible);

        _obsActionOptions.Controls.Add(_obsAudioOptions);
        _obsActionOptions.Controls.Add(_obsVisibilityOptions);
    }

    private Control BuildObsActionSelector()
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
        _obsAction.Margin = Padding.Empty;
        _refreshObsButton = CreateButton("Refresh", RefreshObsCatalogClicked);
        panel.Controls.Add(_obsAction, 0, 0);
        panel.Controls.Add(_refreshObsButton, 1, 0);
        return panel;
    }

    private async Task LoadSelectedObsAsync()
    {
        ObsResourceConfig? resource = SelectedObs;
        _loadingControls = true;

        try
        {
            _obsEditorPanel.Enabled = resource is not null;
            _testObsActionButton.Enabled = resource is not null && !_obsActionTestPending;
            _obsEnabled.Checked = resource?.Enabled ?? false;
            _obsAction.SelectedItem = _obsAction.Items
                .Cast<ObsActionChoice>()
                .FirstOrDefault(choice => choice.Value == resource?.Action);
            _obsMuted.Checked = resource?.Muted ?? false;
            _obsVisible.Checked = resource?.Visible ?? true;
            _obsNotifications.LoadTargets(resource?.Notifications.Target ?? []);
            _obsDiscoveryStatus.Text = resource is null ? "" : "Loading OBS scenes and inputs...";
            ClearObsSelectors();
            UpdateObsControlStates();
        }
        finally
        {
            _loadingControls = false;
        }

        if (resource is null)
            return;

        try
        {
            ObsCatalog catalog = await LoadObsCatalogAsync(false, CancellationToken.None);

            if (!ReferenceEquals(resource, SelectedObs) || IsDisposed)
                return;

            BindObsCatalog(catalog, resource);
            _obsDiscoveryStatus.Text =
                $"OBS WebSocket {catalog.Version}; {catalog.Scenes.Count} scenes and " +
                $"{catalog.AudioInputs.Count} audio sources available.";
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(resource, SelectedObs) || IsDisposed)
                return;

            BindConfiguredObsValues(resource);
            _obsDiscoveryStatus.Text = ex.Message;
        }
    }

    private void BindObsCatalog(ObsCatalog catalog, ObsResourceConfig resource)
    {
        _loadingControls = true;

        try
        {
            BindObsStrings(_obsScene, catalog.Scenes, resource.SceneName);
            BindObsInputs(resource.InputName);
            BindObsSources(resource.SourceName);
            UpdateObsControlStates();
        }
        finally
        {
            _loadingControls = false;
        }
    }

    private void BindConfiguredObsValues(ObsResourceConfig resource)
    {
        _loadingControls = true;

        try
        {
            BindObsStrings(_obsScene, [], resource.SceneName);
            BindObsStrings(_obsInput, [], resource.InputName);
            BindObsStrings(_obsSource, [], resource.SourceName);
            UpdateObsControlStates();
        }
        finally
        {
            _loadingControls = false;
        }
    }

    private static void BindObsStrings(
        ComboBox selector,
        IEnumerable<string> discovered,
        string preferred)
    {
        selector.Items.Clear();

        foreach (string value in discovered)
            selector.Items.Add(value);

        object? selected = selector.Items.Cast<string>().FirstOrDefault(value =>
            string.Equals(value, preferred, StringComparison.OrdinalIgnoreCase));

        if (selected is null && !string.IsNullOrWhiteSpace(preferred))
        {
            selector.Items.Add(preferred);
            selected = preferred;
        }

        selector.SelectedItem = selected ?? (selector.Items.Count > 0 ? selector.Items[0] : null);
    }

    private void BindObsSources(string preferred)
    {
        string sceneName = _obsScene.SelectedItem as string ?? "";
        IEnumerable<string> sources = _obsCatalog?.SceneSources
            .Where(source => string.Equals(
                source.SceneName,
                sceneName,
                StringComparison.OrdinalIgnoreCase
            ))
            .Select(source => source.SourceName) ?? [];
        BindObsStrings(_obsSource, sources, preferred);
    }

    private void BindObsInputs(string preferred)
    {
        string sceneName = _obsScene.SelectedItem as string ?? "";
        var sceneSources = (_obsCatalog?.SceneSources ?? [])
            .Where(source => string.Equals(
                source.SceneName,
                sceneName,
                StringComparison.OrdinalIgnoreCase
            ))
            .Select(source => source.SourceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> inputs = (_obsCatalog?.AudioInputs ?? [])
            .Where(sceneSources.Contains);
        BindObsStrings(_obsInput, inputs, preferred);
    }

    private void ClearObsSelectors()
    {
        _obsScene.Items.Clear();
        _obsInput.Items.Clear();
        _obsSource.Items.Clear();
    }

    private void ObsActionChanged(object? sender, EventArgs e)
    {
        if (_loadingControls || SelectedObs is not ObsResourceConfig resource)
            return;

        resource.Action = (_obsAction.SelectedItem as ObsActionChoice)?.Value ??
            ObsActionType.SwitchScene;
        UpdateObsControlStates();
        ObsResourceFieldChanged(sender, e);
    }

    private void ObsSceneChanged(object? sender, EventArgs e)
    {
        if (_loadingControls || SelectedObs is not ObsResourceConfig resource)
            return;

        resource.SceneName = _obsScene.SelectedItem as string ?? resource.SceneName;
        bool wasLoading = _loadingControls;
        _loadingControls = true;

        try
        {
            BindObsInputs("");
            BindObsSources("");
            resource.InputName = _obsInput.SelectedItem as string ?? "";
            resource.SourceName = _obsSource.SelectedItem as string ?? "";
        }
        finally
        {
            _loadingControls = wasLoading;
        }

        ObsResourceFieldChanged(sender, e);
    }

    private void UpdateObsControlStates()
    {
        ObsActionType action = (_obsAction.SelectedItem as ObsActionChoice)?.Value ??
            ObsActionType.SwitchScene;
        _obsAudioOptions.Visible = action == ObsActionType.SetInputMute;
        _obsVisibilityOptions.Visible = action == ObsActionType.SetSourceVisibility;

        if (_obsAudioOptions.Visible)
            _obsAudioOptions.BringToFront();
        else if (_obsVisibilityOptions.Visible)
            _obsVisibilityOptions.BringToFront();
    }

    private void ObsResourceFieldChanged(object? sender, EventArgs e)
    {
        if (_loadingControls || SelectedObs is not ObsResourceConfig resource)
            return;

        resource.Enabled = _obsEnabled.Checked;
        resource.Action = (_obsAction.SelectedItem as ObsActionChoice)?.Value ?? resource.Action;
        resource.SceneName = _obsScene.SelectedItem as string ?? resource.SceneName;
        resource.InputName = _obsInput.SelectedItem as string ?? resource.InputName;
        resource.SourceName = _obsSource.SelectedItem as string ?? resource.SourceName;
        resource.Muted = _obsMuted.Checked;
        resource.Visible = _obsVisible.Checked;
        resource.Notifications.Target = [.. _obsNotifications.SelectedTargets];
        _resourceList.Refresh();
        UpdateStatus();
    }

    private async void AddObsClicked(object? sender, EventArgs e)
    {
        if (SelectedProfile is not SupervisorProfileConfig profile)
            return;

        try
        {
            ObsCatalog catalog = await LoadObsCatalogAsync(false, CancellationToken.None);
            string sceneName = catalog.Scenes.FirstOrDefault() ?? throw new InvalidOperationException(
                "OBS did not expose any scenes."
            );
            var resource = new ObsResourceConfig { SceneName = sceneName };
            profile.ObsResources.Add(resource);
            BindResourceList(profile, resource);
            LoadSelectedResource();
            _obsAction.Focus();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Cannot add OBS action",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
    }

    private void RemoveObsClicked(object? sender, EventArgs e)
    {
        if (SelectedProfile is not SupervisorProfileConfig profile ||
            SelectedObs is not ObsResourceConfig selected ||
            !ConfirmRemoval("OBS action", ObsResource.GetDisplayName(selected)))
        {
            return;
        }

        profile.ObsResources.Remove(selected);
        ClearRemovedResourceDependencies(profile, selected.ResourceId);
        BindResourceList(profile);
        LoadSelectedResource();
        UpdateStatus();
    }

    private async void RefreshObsCatalogClicked(object? sender, EventArgs e)
    {
        _refreshObsButton.Enabled = false;

        try
        {
            await LoadObsCatalogAsync(true, CancellationToken.None);
            await LoadSelectedObsAsync();
        }
        catch (Exception ex)
        {
            _obsDiscoveryStatus.Text = ex.Message;
        }
        finally
        {
            if (!_refreshObsButton.IsDisposed)
                _refreshObsButton.Enabled = true;
        }
    }

    private async void TestObsActionClicked(object? sender, EventArgs e)
    {
        if (_obsActionTestPending || SelectedObs is not ObsResourceConfig resource)
            return;

        _obsActionTestPending = true;
        _testObsActionButton.Enabled = false;
        _obsDiscoveryStatus.Text = "Applying OBS action...";

        try
        {
            using var client = new ObsWebSocketClient(_configuration.Integrations.Obs);
            await client.ExecuteActionAsync(resource, CancellationToken.None);

            if (IsDisposed)
                return;

            if (ReferenceEquals(resource, SelectedObs))
                _obsDiscoveryStatus.Text = "Test action succeeded; the requested OBS state was left in place.";

            MessageBox.Show(
                this,
                "The OBS action succeeded. AppSupervisor left the requested state in place.",
                "OBS action test succeeded",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
        catch (Exception ex)
        {
            if (!IsDisposed && ReferenceEquals(resource, SelectedObs))
                _obsDiscoveryStatus.Text = ex.Message;

            if (!IsDisposed)
            {
                MessageBox.Show(
                    this,
                    ex.Message,
                    "OBS action test failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
        }
        finally
        {
            _obsActionTestPending = false;

            if (!_testObsActionButton.IsDisposed)
                _testObsActionButton.Enabled = SelectedObs is not null;
        }
    }

    private void TestObsNotificationClicked(object? sender, EventArgs e)
        => PublishTestNotification(_obsNotifications.SelectedTargets, "OBS action");

    private sealed record ObsActionChoice(ObsActionType Value, string DisplayName);
}
