namespace AppSupervisor.ConfigurationUI;

/// <summary>Adds Steam and Windows Store installed-application pickers to helper configuration.</summary>
public sealed partial class ConfigurationEditorForm
{
    /// <summary>Builds the App URI field with Steam and Windows Store discovery buttons.</summary>
    /// <returns>The App URI editor panel.</returns>
    private Control BuildAppUriEditor()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            Margin = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.Controls.Add(_applicationAppUri, 0, 0);
        panel.Controls.Add(CreateButton("Pick Steam...", PickSteamApplicationClicked), 1, 0);
        panel.Controls.Add(CreateButton("Pick Store...", PickStoreApplicationClicked), 2, 0);
        _applicationPath.TextChanged += ClearPackageIdentityWhenAppFieldsEdited;
        _applicationAppUri.TextChanged += ClearPackageIdentityWhenAppFieldsEdited;
        return panel;
    }

    /// <summary>Fills executable and app URI from an installed Steam item.</summary>
    /// <param name="sender">The Steam picker button.</param>
    /// <param name="e">The click event data.</param>
    private void PickSteamApplicationClicked(object? sender, EventArgs e)
    {
        using var picker = new SteamApplicationPickerDialog();

        if (picker.ShowDialog(this) != DialogResult.OK ||
            string.IsNullOrWhiteSpace(picker.SelectedExecutablePath) ||
            string.IsNullOrWhiteSpace(picker.SelectedAppUri))
        {
            return;
        }

        _applicationPath.Text = picker.SelectedExecutablePath;
        _applicationAppUri.Text = picker.SelectedAppUri;
        _applicationArguments.Text = "";
        ClearSelectedPackageIdentity();
    }

    /// <summary>Fills executable, AppsFolder app URI, and update-safe identity from a Store package.</summary>
    /// <param name="sender">The Store picker button.</param>
    /// <param name="e">The click event data.</param>
    private void PickStoreApplicationClicked(object? sender, EventArgs e)
    {
        using var picker = new StoreApplicationPickerDialog();

        if (picker.ShowDialog(this) != DialogResult.OK ||
            string.IsNullOrWhiteSpace(picker.SelectedExecutablePath) ||
            string.IsNullOrWhiteSpace(picker.SelectedAppUri) ||
            SelectedApplication is not ManagedApplicationConfig application)
        {
            return;
        }

        _applicationPath.Text = picker.SelectedExecutablePath;
        _applicationAppUri.Text = picker.SelectedAppUri;
        _applicationArguments.Text = "";
        application.PackageFamilyName = picker.SelectedPackageFamilyName ?? "";
        application.PackageApplicationId = picker.SelectedPackageApplicationId ?? "";
        application.PackageExecutable = picker.SelectedPackageExecutable ?? "";
    }

    /// <summary>Clears package identity after a user manually changes the executable or App URI.</summary>
    /// <param name="sender">The edited executable or App URI text box.</param>
    /// <param name="e">The change event data.</param>
    private void ClearPackageIdentityWhenAppFieldsEdited(object? sender, EventArgs e)
    {
        if (!_loadingControls)
            ClearSelectedPackageIdentity();
    }

    /// <summary>Clears update-safe Store identity from the selected helper.</summary>
    private void ClearSelectedPackageIdentity()
    {
        if (SelectedApplication is not ManagedApplicationConfig application)
            return;

        application.PackageFamilyName = "";
        application.PackageApplicationId = "";
        application.PackageExecutable = "";
    }
}
