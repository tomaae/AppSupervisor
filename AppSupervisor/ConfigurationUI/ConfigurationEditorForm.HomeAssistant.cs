using AppSupervisor.HomeAssistant;
using AppSupervisor.Configuration;

namespace AppSupervisor.ConfigurationUI;

/// <summary>Provides explicit delay and Home Assistant profile-resource editing.</summary>
public sealed partial class ConfigurationEditorForm
{
    private readonly Panel _delayEditorPanel = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _delayEnabled = new()
    {
        Text = "Delay enabled",
        AutoSize = true
    };
    private readonly NumericUpDown _delayDuration = new()
    {
        Minimum = 0,
        Maximum = ConfigurationLimits.MaximumWaitAfterStartupMilliseconds,
        Width = 120,
        ThousandsSeparator = true
    };

    private readonly Panel _homeAssistantEditorPanel = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _homeAssistantEnabled = new()
    {
        Text = "Home Assistant action enabled",
        AutoSize = true
    };
    private readonly ComboBox _homeAssistantService = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
        DisplayMember = nameof(HomeAssistantServiceInfo.DisplayName)
    };
    private readonly ComboBox _homeAssistantEntity = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
        DisplayMember = nameof(HomeAssistantEntityInfo.DisplayName)
    };
    private readonly CheckBox _homeAssistantVerify = new()
    {
        Text = "Verify requested state change",
        AutoSize = true
    };
    private readonly CheckBox _homeAssistantPersistent = new()
    {
        Text = "Keep this state persistent (check every minute)",
        AutoSize = true
    };
    private readonly NotificationTargetsControl _homeAssistantNotifications = new();
    private readonly Label _homeAssistantDiscoveryStatus = new()
    {
        AutoSize = true,
        ForeColor = SystemColors.GrayText
    };
    private Button _refreshHomeAssistantButton = null!;
    private Button _testHomeAssistantActionButton = null!;
    private bool _homeAssistantActionTestPending;

    private Control BuildDelayEditor()
    {
        _delayEditorPanel.Padding = new Padding(14);
        TableLayoutPanel layout = CreateEditorTable();
        AddEditorRow(layout, "", _delayEnabled);
        AddEditorRow(layout, "Duration", BuildMillisecondsEditor(_delayDuration));
        AddEditorRow(layout, "", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            ForeColor = SystemColors.GrayText,
            Text = "This entry delays only the resources that follow it in this profile. Other profiles continue independently."
        });
        _delayEditorPanel.Controls.Add(layout);
        _delayEnabled.CheckedChanged += DelayFieldChanged;
        _delayDuration.ValueChanged += DelayFieldChanged;
        _delayDuration.TextChanged += DelayFieldChanged;
        return _delayEditorPanel;
    }

    private Control BuildHomeAssistantEditor()
    {
        _homeAssistantEditorPanel.Padding = new Padding(14);
        var scrolling = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        TableLayoutPanel layout = CreateEditorTable();
        AddEditorRow(layout, "", _homeAssistantEnabled);
        AddEditorRow(layout, "HA service", BuildHomeAssistantServiceSelector());
        AddEditorRow(layout, "HA entity", _homeAssistantEntity);
        _testHomeAssistantActionButton = CreateButton(
            "Test action",
            TestHomeAssistantActionClicked
        );
        AddEditorRow(layout, "Action", _testHomeAssistantActionButton);
        AddEditorRow(layout, "Verification", _homeAssistantVerify);
        AddEditorRow(layout, "Persistence", _homeAssistantPersistent);
        AddEditorRow(layout, "Notifications", BuildNotificationTestPanel(
            _homeAssistantNotifications,
            TestHomeAssistantNotificationClicked
        ));
        AddEditorRow(layout, "", _homeAssistantDiscoveryStatus);
        AddEditorRow(layout, "", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            ForeColor = SystemColors.GrayText,
            Text = "turn_on and turn_off actions are reversed after the profile close timeout. Home Assistant buttons are stateless, so button.press is issued only when the profile activates."
        });
        scrolling.Controls.Add(layout);
        _homeAssistantEditorPanel.Controls.Add(scrolling);

        _homeAssistantEnabled.CheckedChanged += HomeAssistantResourceFieldChanged;
        _homeAssistantService.SelectedIndexChanged += HomeAssistantServiceChanged;
        _homeAssistantEntity.SelectedIndexChanged += HomeAssistantResourceFieldChanged;
        _homeAssistantVerify.CheckedChanged += HomeAssistantResourceFieldChanged;
        _homeAssistantPersistent.CheckedChanged += HomeAssistantResourceFieldChanged;
        _homeAssistantNotifications.TargetsChanged += HomeAssistantResourceFieldChanged;
        return _homeAssistantEditorPanel;
    }

    private Control BuildHomeAssistantServiceSelector()
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
        _homeAssistantService.Margin = Padding.Empty;
        _refreshHomeAssistantButton = CreateButton(
            "Refresh",
            RefreshHomeAssistantCatalogClicked
        );
        panel.Controls.Add(_homeAssistantService, 0, 0);
        panel.Controls.Add(_refreshHomeAssistantButton, 1, 0);
        return panel;
    }

    private void LoadSelectedDelay()
    {
        DelayResourceConfig? delay = SelectedDelay;
        _loadingControls = true;

        try
        {
            _delayEditorPanel.Enabled = delay is not null;
            _delayEnabled.Checked = delay?.Enabled ?? false;
            _delayDuration.Value = Math.Clamp(
                delay?.DurationMilliseconds ?? 0,
                Decimal.ToInt32(_delayDuration.Minimum),
                Decimal.ToInt32(_delayDuration.Maximum)
            );
        }
        finally
        {
            _loadingControls = false;
        }
    }

    private void DelayFieldChanged(object? sender, EventArgs e)
    {
        if (_loadingControls || SelectedDelay is not DelayResourceConfig delay)
            return;

        delay.Enabled = _delayEnabled.Checked;
        delay.DurationMilliseconds = ReadDisplayedNumber(_delayDuration);
        _resourceList.Refresh();
        UpdateStatus();
    }

    private async Task LoadSelectedHomeAssistantAsync()
    {
        HomeAssistantResourceConfig? resource = SelectedHomeAssistant;
        _loadingControls = true;

        try
        {
            _homeAssistantEditorPanel.Enabled = resource is not null;
            _testHomeAssistantActionButton.Enabled =
                resource is not null && !_homeAssistantActionTestPending;
            _homeAssistantEnabled.Checked = resource?.Enabled ?? false;
            _homeAssistantVerify.Checked = resource?.VerifyStateChange ?? false;
            _homeAssistantPersistent.Checked = resource?.Persistent ?? false;
            _homeAssistantNotifications.LoadTargets(resource?.Notifications.Target ?? []);
            _homeAssistantDiscoveryStatus.Text = resource is null
                ? ""
                : "Loading Home Assistant services and entities...";
            ClearHomeAssistantSelectors();
        }
        finally
        {
            _loadingControls = false;
        }

        if (resource is null)
            return;

        try
        {
            HomeAssistantCatalog catalog = await LoadHomeAssistantCatalogAsync(
                forceRefresh: false,
                CancellationToken.None
            );

            if (!ReferenceEquals(resource, SelectedHomeAssistant) || IsDisposed)
                return;

            BindHomeAssistantServices(catalog, resource.Service);
            BindHomeAssistantEntities(catalog, resource.EntityId);
            _homeAssistantDiscoveryStatus.Text =
                $"Home Assistant {catalog.Version}; {catalog.Services.Count} supported services available.";
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(resource, SelectedHomeAssistant) || IsDisposed)
                return;

            BindConfiguredHomeAssistantValues(resource);
            _homeAssistantDiscoveryStatus.Text = ex.Message;
        }
    }

    private void BindHomeAssistantServices(HomeAssistantCatalog catalog, string preferredService)
    {
        _loadingControls = true;

        try
        {
            _homeAssistantService.Items.Clear();

            foreach (HomeAssistantServiceInfo service in catalog.Services)
                _homeAssistantService.Items.Add(service);

            HomeAssistantServiceInfo? selected = _homeAssistantService.Items
                .Cast<HomeAssistantServiceInfo>()
                .FirstOrDefault(item => string.Equals(
                    item.Service,
                    preferredService,
                    StringComparison.OrdinalIgnoreCase
                ));

            if (selected is null && !string.IsNullOrWhiteSpace(preferredService))
            {
                string domain = preferredService.Split('.', 2)[0];
                selected = new HomeAssistantServiceInfo(preferredService, [domain]);
                _homeAssistantService.Items.Add(selected);
            }

            _homeAssistantService.SelectedItem = selected ??
                (_homeAssistantService.Items.Count > 0 ? _homeAssistantService.Items[0] : null);
            UpdateHomeAssistantStateOptions();
        }
        finally
        {
            _loadingControls = false;
        }
    }

    private void BindHomeAssistantEntities(HomeAssistantCatalog catalog, string preferredEntityId)
    {
        string[] domains = (_homeAssistantService.SelectedItem as HomeAssistantServiceInfo)?
            .EntityDomains.ToArray() ?? [];
        _loadingControls = true;

        try
        {
            _homeAssistantEntity.Items.Clear();

            foreach (HomeAssistantEntityInfo entity in catalog.Entities.Where(entity =>
                domains.Contains(
                    entity.EntityId.Split('.', 2)[0],
                    StringComparer.OrdinalIgnoreCase
                )))
            {
                _homeAssistantEntity.Items.Add(entity);
            }

            HomeAssistantEntityInfo? selected = _homeAssistantEntity.Items
                .Cast<HomeAssistantEntityInfo>()
                .FirstOrDefault(item => string.Equals(
                    item.EntityId,
                    preferredEntityId,
                    StringComparison.OrdinalIgnoreCase
                ));

            if (selected is null && !string.IsNullOrWhiteSpace(preferredEntityId))
            {
                selected = new HomeAssistantEntityInfo(
                    preferredEntityId,
                    SelectedHomeAssistant?.EntityName ?? "",
                    "not discovered"
                );
                _homeAssistantEntity.Items.Add(selected);
            }

            _homeAssistantEntity.SelectedItem = selected ??
                (_homeAssistantEntity.Items.Count > 0 ? _homeAssistantEntity.Items[0] : null);
        }
        finally
        {
            _loadingControls = false;
        }
    }

    private void BindConfiguredHomeAssistantValues(HomeAssistantResourceConfig resource)
    {
        _loadingControls = true;

        try
        {
            _homeAssistantService.Items.Clear();
            _homeAssistantEntity.Items.Clear();

            if (!string.IsNullOrWhiteSpace(resource.Service))
            {
                string domain = resource.Service.Split('.', 2)[0];
                var service = new HomeAssistantServiceInfo(resource.Service, [domain]);
                _homeAssistantService.Items.Add(service);
                _homeAssistantService.SelectedItem = service;
            }

            if (!string.IsNullOrWhiteSpace(resource.EntityId))
            {
                var entity = new HomeAssistantEntityInfo(
                    resource.EntityId,
                    resource.EntityName,
                    "not discovered"
                );
                _homeAssistantEntity.Items.Add(entity);
                _homeAssistantEntity.SelectedItem = entity;
            }

            UpdateHomeAssistantStateOptions();
        }
        finally
        {
            _loadingControls = false;
        }
    }

    private void ClearHomeAssistantSelectors()
    {
        _homeAssistantService.Items.Clear();
        _homeAssistantEntity.Items.Clear();
    }

    private void HomeAssistantServiceChanged(object? sender, EventArgs e)
    {
        if (_loadingControls || SelectedHomeAssistant is not HomeAssistantResourceConfig resource)
            return;

        resource.Service = (_homeAssistantService.SelectedItem as HomeAssistantServiceInfo)?.Service ?? "";
        resource.EntityId = "";
        resource.EntityName = "";
        UpdateHomeAssistantStateOptions();

        if (_homeAssistantCatalog is not null)
            BindHomeAssistantEntities(_homeAssistantCatalog, "");

        HomeAssistantResourceFieldChanged(sender, e);
    }

    private void UpdateHomeAssistantStateOptions()
    {
        string service = (_homeAssistantService.SelectedItem as HomeAssistantServiceInfo)?.Service ?? "";
        bool stateful = service.EndsWith(".turn_on", StringComparison.OrdinalIgnoreCase) ||
            service.EndsWith(".turn_off", StringComparison.OrdinalIgnoreCase);
        _homeAssistantVerify.Enabled = stateful;
        _homeAssistantPersistent.Enabled = stateful;

        if (!stateful)
        {
            _homeAssistantVerify.Checked = false;
            _homeAssistantPersistent.Checked = false;
        }
    }

    private void HomeAssistantResourceFieldChanged(object? sender, EventArgs e)
    {
        if (_loadingControls || SelectedHomeAssistant is not HomeAssistantResourceConfig resource)
            return;

        resource.Enabled = _homeAssistantEnabled.Checked;
        resource.Service = (_homeAssistantService.SelectedItem as HomeAssistantServiceInfo)?.Service ??
            resource.Service;

        if (_homeAssistantEntity.SelectedItem is HomeAssistantEntityInfo entity)
        {
            resource.EntityId = entity.EntityId;
            resource.EntityName = entity.FriendlyName;
        }

        resource.VerifyStateChange = _homeAssistantVerify.Checked;
        resource.Persistent = _homeAssistantPersistent.Checked;
        resource.Notifications.Target = [.. _homeAssistantNotifications.SelectedTargets];
        _resourceList.Refresh();
        UpdateStatus();
    }

    private void AddDelayClicked(object? sender, EventArgs e)
    {
        if (SelectedProfile is not SupervisorProfileConfig profile)
            return;

        var delay = new DelayResourceConfig();
        profile.Delays.Add(delay);
        BindResourceList(profile, delay);
        LoadSelectedResource();
        _delayDuration.Focus();
        UpdateStatus();
    }

    private async void AddHomeAssistantClicked(object? sender, EventArgs e)
    {
        if (SelectedProfile is not SupervisorProfileConfig profile)
            return;

        try
        {
            HomeAssistantCatalog catalog = await LoadHomeAssistantCatalogAsync(
                forceRefresh: false,
                CancellationToken.None
            );
            HomeAssistantServiceInfo service = catalog.Services.FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "Home Assistant did not expose a supported turn_on, turn_off, or button.press service."
                );
            HomeAssistantEntityInfo entity = catalog.Entities.FirstOrDefault(candidate =>
                service.EntityDomains.Contains(
                    candidate.EntityId.Split('.', 2)[0],
                    StringComparer.OrdinalIgnoreCase
                )) ?? throw new InvalidOperationException(
                    $"Home Assistant did not expose an entity compatible with {service.Service}."
                );
            var resource = new HomeAssistantResourceConfig
            {
                Service = service.Service,
                EntityId = entity.EntityId,
                EntityName = entity.FriendlyName
            };
            profile.HomeAssistantResources.Add(resource);
            BindResourceList(profile, resource);
            LoadSelectedResource();
            _homeAssistantService.Focus();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Cannot add Home Assistant resource",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
    }

    private void RemoveDelayClicked(object? sender, EventArgs e)
    {
        if (SelectedProfile is not SupervisorProfileConfig profile ||
            SelectedDelay is not DelayResourceConfig selected ||
            !ConfirmRemoval("delay", $"{selected.DurationMilliseconds:N0} ms"))
        {
            return;
        }

        profile.Delays.Remove(selected);
        ClearRemovedResourceDependencies(profile, selected.ResourceId);
        BindResourceList(profile);
        LoadSelectedResource();
        UpdateStatus();
    }

    private void RemoveHomeAssistantClicked(object? sender, EventArgs e)
    {
        if (SelectedProfile is not SupervisorProfileConfig profile ||
            SelectedHomeAssistant is not HomeAssistantResourceConfig selected ||
            !ConfirmRemoval(
                "Home Assistant resource",
                DisplayName(selected.EntityName, selected.EntityId)
            ))
        {
            return;
        }

        profile.HomeAssistantResources.Remove(selected);
        ClearRemovedResourceDependencies(profile, selected.ResourceId);
        BindResourceList(profile);
        LoadSelectedResource();
        UpdateStatus();
    }

    private async void RefreshHomeAssistantCatalogClicked(object? sender, EventArgs e)
    {
        _refreshHomeAssistantButton.Enabled = false;

        try
        {
            await LoadHomeAssistantCatalogAsync(forceRefresh: true, CancellationToken.None);
            await LoadSelectedHomeAssistantAsync();
        }
        catch (Exception ex)
        {
            _homeAssistantDiscoveryStatus.Text = ex.Message;
        }
        finally
        {
            if (!_refreshHomeAssistantButton.IsDisposed)
                _refreshHomeAssistantButton.Enabled = true;
        }
    }

    private void TestHomeAssistantNotificationClicked(object? sender, EventArgs e)
        => PublishTestNotification(
            _homeAssistantNotifications.SelectedTargets,
            "Home Assistant resource"
        );

    /// <summary>Temporarily applies the selected stateful Home Assistant action and restores its original state.</summary>
    /// <param name="sender">The Test action button.</param>
    /// <param name="e">The click event data.</param>
    private async void TestHomeAssistantActionClicked(object? sender, EventArgs e)
    {
        if (_homeAssistantActionTestPending ||
            SelectedHomeAssistant is not HomeAssistantResourceConfig resource)
        {
            return;
        }

        string service = resource.Service.Trim();
        string entityId = resource.EntityId.Trim();
        HomeAssistantIntegrationConfig integration = _configuration.Integrations.HomeAssistant;

        if (string.IsNullOrWhiteSpace(integration.Url) ||
            string.IsNullOrWhiteSpace(integration.Token) ||
            string.IsNullOrWhiteSpace(service) ||
            string.IsNullOrWhiteSpace(entityId))
        {
            MessageBox.Show(
                this,
                "Select a Home Assistant service and entity, and configure the global URL and token first.",
                "Cannot test Home Assistant action",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
            return;
        }

        _homeAssistantActionTestPending = true;
        _testHomeAssistantActionButton.Enabled = false;
        _homeAssistantDiscoveryStatus.Text =
            $"Testing {service} for five seconds, then restoring the original state...";

        try
        {
            using var client = new HomeAssistantClient(new HomeAssistantIntegrationConfig
            {
                Url = integration.Url.Trim(),
                Token = integration.Token.Trim()
            });
            HomeAssistantActionTestResult result = await HomeAssistantActionTester.RunAsync(
                client,
                service,
                entityId,
                CancellationToken.None
            );

            if (IsDisposed)
                return;

            if (!result.Changed)
            {
                if (ReferenceEquals(resource, SelectedHomeAssistant))
                    _homeAssistantDiscoveryStatus.Text = "No test action was needed.";

                MessageBox.Show(
                    this,
                    $"{entityId} is already '{result.DesiredState}'. The configured action would not change its state, so no test was performed.",
                    "Home Assistant action unchanged",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
                return;
            }

            if (ReferenceEquals(resource, SelectedHomeAssistant))
            {
                _homeAssistantDiscoveryStatus.Text =
                    $"Test succeeded; restoration to '{result.OriginalState}' was requested.";
            }

            MessageBox.Show(
                this,
                $"The action changed {entityId} from '{result.OriginalState}' to " +
                $"'{result.DesiredState}' for five seconds. Restoration to " +
                $"'{result.OriginalState}' was then requested.",
                "Home Assistant action test succeeded",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
        catch (Exception ex)
        {
            if (IsDisposed)
                return;

            if (ReferenceEquals(resource, SelectedHomeAssistant))
                _homeAssistantDiscoveryStatus.Text = ex.Message;

            MessageBox.Show(
                this,
                ex.Message,
                "Home Assistant action test failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }
        finally
        {
            _homeAssistantActionTestPending = false;

            if (!_testHomeAssistantActionButton.IsDisposed)
            {
                _testHomeAssistantActionButton.Enabled =
                    SelectedHomeAssistant is not null;
            }
        }
    }
}
