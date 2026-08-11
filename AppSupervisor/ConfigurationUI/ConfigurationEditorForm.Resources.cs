using AppSupervisor.Configuration;

namespace AppSupervisor.ConfigurationUI;

/// <summary>
/// Provides the combined cross-type resource list, ordering controls, startup wait, and dependency editor.
/// </summary>
public sealed partial class ConfigurationEditorForm
{
    private readonly ListBox _resourceList = new() { Dock = DockStyle.Fill };
    private readonly Panel _resourceEditorPanel = new() { Dock = DockStyle.Fill };
    private readonly Panel _resourceTypeEditorPanel = new() { Dock = DockStyle.Fill };
    private readonly Label _resourceTypeLabel = new() { AutoSize = true };
    private readonly ComboBox _resourceDependency = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
        DisplayMember = nameof(ResourceDependencyChoice.DisplayName)
    };
    private readonly NumericUpDown _resourceWaitAfterStartup = new()
    {
        Minimum = 0,
        Maximum = ConfigurationLimits.MaximumWaitAfterStartupMilliseconds,
        Width = 120,
        ThousandsSeparator = true
    };
    private Button _moveResourceUpButton = null!;
    private Button _moveResourceDownButton = null!;
    private ManagedResourceConfig? _draggedResource;
    private Point _resourceDragStart;

    private bool _resourceDragActive;
    /// <summary>Builds the unified ordered application and service page.</summary>
    /// <returns>The Resources tab page.</returns>
    private TabPage BuildResourcesPage()
    {
        var page = new TabPage("Resources");
        var split = CreateListEditorSplit();
        split.Panel1.Controls.Add(BuildResourceListPanel());
        split.Panel2.Controls.Add(BuildResourceEditor());
        page.Controls.Add(split);
        return page;
    }

    /// <summary>Builds the ordered resource list and add, remove, and move commands.</summary>
    /// <returns>The left-side resource collection panel.</returns>
    private Control BuildResourceListPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        var buttons = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 96,
            ColumnCount = 2,
            RowCount = 3,
            Margin = Padding.Empty
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
        buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
        buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 33.34F));

        Button addApplicationButton = CreateButton("Add application", AddApplicationClicked);
        Button addServiceButton = CreateButton("Add service", AddServiceClicked);
        Button removeButton = CreateButton("Remove", RemoveResourceClicked);
        _moveResourceUpButton = CreateButton("Move up", MoveResourceUpClicked);
        _moveResourceDownButton = CreateButton("Move down", MoveResourceDownClicked);

        foreach (Button button in new[]
        {
            addApplicationButton,
            addServiceButton,
            _moveResourceUpButton,
            _moveResourceDownButton,
            removeButton
        })
        {
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(3);
        }

        buttons.Controls.Add(_moveResourceUpButton, 0, 0);
        buttons.Controls.Add(_moveResourceDownButton, 1, 0);
        buttons.Controls.Add(addApplicationButton, 0, 1);
        buttons.Controls.Add(addServiceButton, 1, 1);
        buttons.Controls.Add(removeButton, 0, 2);
        buttons.SetColumnSpan(removeButton, 2);
        panel.Controls.Add(_resourceList);
        panel.Controls.Add(buttons);
        return panel;
    }

    /// <summary>Builds shared sequencing fields above the selected resource's type-specific settings.</summary>
    /// <returns>The complete right-side resource editor.</returns>
    private Control BuildResourceEditor()
    {
        var startupPanel = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(14, 12, 14, 4)
        };
        TableLayoutPanel startupLayout = CreateEditorTable();
        AddEditorRow(startupLayout, "Type", _resourceTypeLabel);
        AddEditorRow(startupLayout, "Dependency", _resourceDependency);
        AddEditorRow(
            startupLayout,
            "Wait after startup",
            BuildMillisecondsEditor(_resourceWaitAfterStartup)
        );
        AddEditorRow(startupLayout, "", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Resources start from top to bottom. A dependency can only be an earlier resource. The wait is nonblocking and delays only the next resource in this profile."
        });
        startupPanel.Controls.Add(startupLayout);

        var scrollableSection = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = SystemColors.ControlDark,
            Padding = new Padding(0, 1, 0, 0)
        };
        _resourceTypeEditorPanel.BackColor = SystemColors.Control;
        _resourceTypeEditorPanel.Controls.Add(BuildApplicationEditor());
        _resourceTypeEditorPanel.Controls.Add(BuildServiceEditor());
        scrollableSection.Controls.Add(_resourceTypeEditorPanel);
        _resourceEditorPanel.Controls.Add(scrollableSection);
        _resourceEditorPanel.Controls.Add(startupPanel);
        return _resourceEditorPanel;
    }

    /// <summary>Builds one numeric millisecond editor with an explicit unit suffix.</summary>
    /// <param name="numeric">The bounded millisecond input.</param>
    /// <returns>A compact numeric and unit row.</returns>
    private static Control BuildMillisecondsEditor(NumericUpDown numeric)
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
            Text = "milliseconds",
            AutoSize = true,
            Margin = new Padding(4, 7, 0, 0)
        });
        return panel;
    }

    /// <summary>Rebuilds and normalizes the selected profile's cross-type resource order.</summary>
    /// <param name="profile">The selected profile, or null when no profile exists.</param>
    /// <param name="preferred">The application or service to keep selected.</param>
    private void BindResourceList(
        SupervisorProfileConfig? profile,
        ManagedResourceConfig? preferred = null)
    {
        _resourceList.Items.Clear();

        if (profile is null)
        {
            UpdateResourceMoveButtons();
            return;
        }

        List<ManagedResourceConfig> resources = GetOrderedResources(profile);

        for (int index = 0; index < resources.Count; index++)
        {
            resources[index].StartupOrder = index;
            _resourceList.Items.Add(resources[index]);
        }

        if (preferred is not null && _resourceList.Items.Contains(preferred))
            _resourceList.SelectedItem = preferred;
        else if (_resourceList.Items.Count > 0)
            _resourceList.SelectedIndex = 0;

        UpdateResourceMoveButtons();
    }

    /// <summary>Returns applications and services in their effective cross-type startup order.</summary>
    /// <param name="profile">The profile whose resources are required.</param>
    /// <returns>A stable ordered list that preserves application-before-service legacy order when unspecified.</returns>
    private static List<ManagedResourceConfig> GetOrderedResources(SupervisorProfileConfig profile)
    {
        return profile.Applications
            .Cast<ManagedResourceConfig>()
            .Concat(profile.Services)
            .Select((resource, stableOrder) => (resource, stableOrder))
            .OrderBy(item => item.resource.StartupOrder < 0
                ? int.MaxValue
                : item.resource.StartupOrder)
            .ThenBy(item => item.stableOrder)
            .Select(item => item.resource)
            .ToList();
    }

    /// <summary>Loads shared sequencing controls and the selected resource's matching type editor.</summary>
    private void LoadSelectedResource()
    {
        ManagedResourceConfig? resource = SelectedResource;
        _loadingControls = true;

        try
        {
            bool available = resource is not null;
            _resourceEditorPanel.Enabled = available;
            _resourceTypeLabel.Text = resource switch
            {
                ManagedApplicationConfig => "Application",
                ManagedServiceConfig => "Windows service",
                _ => ""
            };
            _resourceWaitAfterStartup.Value = Math.Clamp(
                resource?.WaitAfterStartupMilliseconds ?? 0,
                Decimal.ToInt32(_resourceWaitAfterStartup.Minimum),
                Decimal.ToInt32(_resourceWaitAfterStartup.Maximum)
            );
            BindResourceDependency(resource);
            _applicationEditorPanel.Visible = resource is ManagedApplicationConfig;
            _serviceEditorPanel.Visible = resource is ManagedServiceConfig;

            if (_applicationEditorPanel.Visible)
                _applicationEditorPanel.BringToFront();
            else if (_serviceEditorPanel.Visible)
                _serviceEditorPanel.BringToFront();
        }
        finally
        {
            _loadingControls = false;
        }

        LoadSelectedApplication();
        LoadSelectedService();
        UpdateResourceMoveButtons();
    }

    /// <summary>Populates dependency choices with only resources positioned before the selected entry.</summary>
    /// <param name="resource">The selected resource, or null when none is selected.</param>
    private void BindResourceDependency(ManagedResourceConfig? resource)
    {
        _resourceDependency.Items.Clear();
        _resourceDependency.Items.Add(new ResourceDependencyChoice("", "(none)"));

        if (resource is not null && SelectedProfile is SupervisorProfileConfig profile)
        {
            foreach (ManagedResourceConfig earlierResource in GetOrderedResources(profile)
                .TakeWhile(candidate => !ReferenceEquals(candidate, resource)))
            {
                _resourceDependency.Items.Add(new ResourceDependencyChoice(
                    earlierResource.ResourceId,
                    GetResourceDisplayName(earlierResource)
                ));
            }
        }

        ResourceDependencyChoice? selected = _resourceDependency.Items
            .Cast<ResourceDependencyChoice>()
            .FirstOrDefault(choice => string.Equals(
                choice.ResourceId,
                resource?.DependencyResourceId,
                StringComparison.OrdinalIgnoreCase
            ));
        _resourceDependency.SelectedItem = selected ?? _resourceDependency.Items[0];
    }

    /// <summary>Writes shared wait and dependency values to the selected resource.</summary>
    /// <param name="sender">The changed sequencing control.</param>
    /// <param name="e">The change event data.</param>
    private void ResourceStartupFieldChanged(object? sender, EventArgs e)
    {
        if (_loadingControls || SelectedResource is not ManagedResourceConfig resource)
            return;

        resource.WaitAfterStartupMilliseconds =
            Decimal.ToInt32(_resourceWaitAfterStartup.Value);
        resource.DependencyResourceId =
            (_resourceDependency.SelectedItem as ResourceDependencyChoice)?.ResourceId ?? "";
        UpdateStatus();
    }

    /// <summary>Loads the newly selected application or service and its sequencing settings.</summary>
    /// <param name="sender">The combined resource list.</param>
    /// <param name="e">The selection-change event data.</param>
    private void ResourceSelectionChanged(object? sender, EventArgs e)
    {
        if (!_loadingControls)
            LoadSelectedResource();
    }

    /// <summary>Formats a combined list entry with its type, name, and disabled state.</summary>
    /// <param name="sender">The combined resource list.</param>
    /// <param name="e">The formatting event data.</param>
    private void ResourceListFormat(object? sender, ListControlConvertEventArgs e)
    {
        if (e.ListItem is not ManagedResourceConfig resource)
            return;

        e.Value = GetResourceDisplayName(resource);
    }


    /// <summary>Records the selected resource and pointer origin for a possible drag operation.</summary>
    /// <param name="sender">The combined resource list.</param>
    /// <param name="e">The mouse-down event data.</param>
    private void ResourceListMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;

        int index = _resourceList.IndexFromPoint(e.Location);
        _draggedResource = index >= 0
            ? _resourceList.Items[index] as ManagedResourceConfig
            : null;
        _resourceDragStart = e.Location;
        _resourceDragActive = false;
        _resourceList.Capture = _draggedResource is not null;
    }

    /// <summary>Directly repositions the dragged entry as the pointer crosses list rows.</summary>
    /// <param name="sender">The combined resource list.</param>
    /// <param name="e">The mouse-move event data.</param>
    private void ResourceListMouseMove(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _draggedResource is null)
            return;

        if (!_resourceDragActive)
        {
            Size dragSize = SystemInformation.DragSize;
            var dragThreshold = new Rectangle(
                _resourceDragStart.X - dragSize.Width / 2,
                _resourceDragStart.Y - dragSize.Height / 2,
                dragSize.Width,
                dragSize.Height
            );

            if (dragThreshold.Contains(e.Location))
                return;

            _resourceDragActive = true;
        }

        int targetIndex = _resourceList.IndexFromPoint(e.Location);

        if (targetIndex < 0)
        {
            if (e.Y < 0)
                targetIndex = 0;
            else if (e.Y >= _resourceList.ClientSize.Height)
                targetIndex = _resourceList.Items.Count - 1;
            else
                return;
        }

        int currentIndex = _resourceList.Items.IndexOf(_draggedResource);

        if (currentIndex < 0 || targetIndex < 0 || currentIndex == targetIndex)
            return;

        bool wasLoading = _loadingControls;
        _loadingControls = true;

        try
        {
            _resourceList.BeginUpdate();
            _resourceList.Items.RemoveAt(currentIndex);
            _resourceList.Items.Insert(targetIndex, _draggedResource);
            _resourceList.SelectedItem = _draggedResource;
        }
        finally
        {
            _resourceList.EndUpdate();
            _loadingControls = wasLoading;
        }

        UpdateResourceMoveButtons();
    }

    /// <summary>Commits the visible resource sequence when the left mouse button is released.</summary>
    /// <param name="sender">The combined resource list.</param>
    /// <param name="e">The mouse-up event data.</param>
    private void ResourceListMouseUp(object? sender, MouseEventArgs e)
    {
        ManagedResourceConfig? draggedResource = _draggedResource;
        bool shouldCommit = e.Button == MouseButtons.Left &&
            _resourceDragActive &&
            draggedResource is not null &&
            SelectedProfile is not null;
        EndResourceListDrag();

        if (!shouldCommit || SelectedProfile is not SupervisorProfileConfig profile)
            return;

        List<ManagedResourceConfig> resources = _resourceList.Items
            .Cast<ManagedResourceConfig>()
            .ToList();
        ApplyResourceOrder(profile, resources, draggedResource!);
    }

    /// <summary>Clears temporary pointer-capture state after a completed or cancelled list drag.</summary>
    private void EndResourceListDrag()
    {
        _resourceList.Capture = false;
        _draggedResource = null;
        _resourceDragActive = false;
    }

    /// <summary>Normalizes and displays a changed resource sequence.</summary>
    /// <param name="profile">The profile whose sequence changed.</param>
    /// <param name="resources">The resources in their new order.</param>
    /// <param name="selected">The resource to keep selected.</param>
    private void ApplyResourceOrder(
        SupervisorProfileConfig profile,
        IReadOnlyList<ManagedResourceConfig> resources,
        ManagedResourceConfig selected)
    {
        for (int index = 0; index < resources.Count; index++)
            resources[index].StartupOrder = index;

        ClearDependenciesThatNoLongerPointBackward(resources);
        BindResourceList(profile, selected);
        LoadSelectedResource();
        UpdateStatus();
    }
    /// <summary>Returns one application or service display label for lists and dependency choices.</summary>
    /// <param name="resource">The application or service configuration.</param>
    /// <returns>A type-qualified name with disabled state when applicable.</returns>
    private static string GetResourceDisplayName(ManagedResourceConfig resource)
    {
        string name = resource switch
        {
            ManagedApplicationConfig application =>
                $"[Application] {SafeFileName(application.Path, "New application")}",
            ManagedServiceConfig service =>
                $"[Service] {DisplayName(service.ServiceName, "New service")}",
            _ => "[Resource]"
        };
        return name + (resource.Enabled ? "" : " (disabled)");
    }

    /// <summary>Removes the selected application or service through its existing confirmation flow.</summary>
    /// <param name="sender">The Remove button.</param>
    /// <param name="e">The click event data.</param>
    private void RemoveResourceClicked(object? sender, EventArgs e)
    {
        if (SelectedResource is ManagedApplicationConfig)
            RemoveApplicationClicked(sender, e);
        else if (SelectedResource is ManagedServiceConfig)
            RemoveServiceClicked(sender, e);
    }

    /// <summary>Moves the selected resource one position earlier.</summary>
    /// <param name="sender">The Move up button.</param>
    /// <param name="e">The click event data.</param>
    private void MoveResourceUpClicked(object? sender, EventArgs e)
    {
        MoveSelectedResource(-1);
    }

    /// <summary>Moves the selected resource one position later.</summary>
    /// <param name="sender">The Move down button.</param>
    /// <param name="e">The click event data.</param>
    private void MoveResourceDownClicked(object? sender, EventArgs e)
    {
        MoveSelectedResource(1);
    }

    /// <summary>Moves one resource, normalizes order, and clears dependencies that no longer point backward.</summary>
    /// <param name="offset">Negative one to move up or positive one to move down.</param>
    private void MoveSelectedResource(int offset)
    {
        if (SelectedProfile is not SupervisorProfileConfig profile ||
            SelectedResource is not ManagedResourceConfig selected)
        {
            return;
        }

        List<ManagedResourceConfig> resources = GetOrderedResources(profile);
        int currentIndex = resources.IndexOf(selected);
        int targetIndex = currentIndex + offset;

        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= resources.Count)
            return;

        (resources[currentIndex], resources[targetIndex]) =
            (resources[targetIndex], resources[currentIndex]);

        ApplyResourceOrder(profile, resources, selected);
    }

    /// <summary>Clears dependencies invalidated by moving their target after the dependent resource.</summary>
    /// <param name="resources">The complete normalized resource order.</param>
    private static void ClearDependenciesThatNoLongerPointBackward(
        IReadOnlyList<ManagedResourceConfig> resources)
    {
        var orderById = resources
            .Select((resource, index) => (resource.ResourceId, index))
            .ToDictionary(item => item.ResourceId, item => item.index, StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < resources.Count; index++)
        {
            ManagedResourceConfig resource = resources[index];

            if (!string.IsNullOrWhiteSpace(resource.DependencyResourceId) &&
                (!orderById.TryGetValue(resource.DependencyResourceId, out int dependencyIndex) ||
                    dependencyIndex >= index))
            {
                resource.DependencyResourceId = "";
            }
        }
    }

    /// <summary>Clears references to a removed resource from all remaining resources in the profile.</summary>
    /// <param name="profile">The profile that contained the removed resource.</param>
    /// <param name="removedResourceId">The removed stable resource identifier.</param>
    private static void ClearRemovedResourceDependencies(
        SupervisorProfileConfig profile,
        string removedResourceId)
    {
        foreach (ManagedResourceConfig resource in profile.Applications
            .Cast<ManagedResourceConfig>()
            .Concat(profile.Services))
        {
            if (string.Equals(
                resource.DependencyResourceId,
                removedResourceId,
                StringComparison.OrdinalIgnoreCase))
            {
                resource.DependencyResourceId = "";
            }
        }
    }

    /// <summary>Enables move commands only when the selected resource can move in that direction.</summary>
    private void UpdateResourceMoveButtons()
    {
        int index = _resourceList.SelectedIndex;
        _moveResourceUpButton.Enabled = index > 0;
        _moveResourceDownButton.Enabled = index >= 0 && index < _resourceList.Items.Count - 1;
    }

    /// <summary>Represents one dependency dropdown choice.</summary>
    private sealed record ResourceDependencyChoice(string ResourceId, string DisplayName);
}
