using AppSupervisor.Store;

namespace AppSupervisor.ConfigurationUI;

/// <summary>Lets the user choose a launchable application from packages installed for the current Windows user.</summary>
internal sealed class StoreApplicationPickerDialog : Form
{
    private readonly TextBox _filterTextBox;
    private readonly CheckBox _showSystemApplications;
    private readonly ListView _applicationList;
    private readonly PickerLoadingOverlay _loadingOverlay;
    private readonly Label _statusLabel;
    private readonly Button _selectButton;
    private readonly CancellationTokenSource _loadCancellation = new();
    private readonly ExecutableIconList _icons;
    private IReadOnlyList<InstalledStoreApplication> _applications = [];

    /// <summary>Creates the package picker and begins discovery when its window is first displayed.</summary>
    public StoreApplicationPickerDialog()
    {
        Text = "Choose installed Windows Store application";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(780, 500);
        Size = new Size(980, 650);
        AutoScaleMode = AutoScaleMode.Dpi;
        _icons = new ExecutableIconList(DeviceDpi);

        var topPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            Padding = new Padding(10)
        };
        topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        topPanel.Controls.Add(new Label
        {
            Text = "Filter:",
            AutoSize = true,
            Margin = new Padding(0, 6, 8, 0)
        }, 0, 0);
        _filterTextBox = new TextBox { Dock = DockStyle.Fill, Enabled = false };
        _filterTextBox.TextChanged += FilterChanged;
        topPanel.Controls.Add(_filterTextBox, 1, 0);
        _showSystemApplications = new CheckBox
        {
            Text = "Show Microsoft/system applications",
            AutoSize = true,
            Enabled = false,
            Margin = new Padding(10, 5, 0, 0)
        };
        _showSystemApplications.CheckedChanged += FilterChanged;
        topPanel.Controls.Add(_showSystemApplications, 2, 0);

        _applicationList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            Enabled = false,
            SmallImageList = _icons.Images
        };
        _applicationList.Columns.Add("Application", 280);
        _applicationList.Columns.Add("Package", 270);
        _applicationList.Columns.Add("Executable path", 390);
        _applicationList.SelectedIndexChanged += ApplicationSelectionChanged;
        _applicationList.DoubleClick += ApplicationDoubleClicked;
        var resultPanel = new Panel { Dock = DockStyle.Fill };
        resultPanel.Controls.Add(_applicationList);
        _loadingOverlay = new PickerLoadingOverlay(resultPanel);

        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(10, 6, 10, 2),
            Text = "Discovering installed Windows Store applications..."
        };

        var buttons = new FlowLayoutPanel
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
        _selectButton = new Button { Text = "Select", AutoSize = true, Enabled = false };
        _selectButton.Click += SelectClicked;
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(_selectButton);

        Controls.Add(resultPanel);
        Controls.Add(topPanel);
        Controls.Add(_statusLabel);
        Controls.Add(buttons);
        AcceptButton = _selectButton;
        CancelButton = cancelButton;
        Shown += DialogShown;
    }

    /// <summary>Gets the selected current-version executable path.</summary>
    public string? SelectedExecutablePath { get; private set; }

    /// <summary>Gets the selected Explorer AppsFolder app URI.</summary>
    public string? SelectedAppUri { get; private set; }

    /// <summary>Gets the selected package family used for update-safe path resolution.</summary>
    public string? SelectedPackageFamilyName { get; private set; }

    /// <summary>Gets the selected package application identifier.</summary>
    public string? SelectedPackageApplicationId { get; private set; }

    /// <summary>Gets the selected executable path relative to its versioned package installation.</summary>
    public string? SelectedPackageExecutable { get; private set; }

    /// <summary>Cancels package discovery and releases the dialog controls.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loadCancellation.Cancel();
            _loadCancellation.Dispose();
            _icons.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Loads installed package applications on a worker thread after the dialog is visible.</summary>
    /// <param name="sender">The displayed dialog.</param>
    /// <param name="e">The shown event data.</param>
    private async void DialogShown(object? sender, EventArgs e)
    {
        Shown -= DialogShown;

        try
        {
            _applications = await Task.Run(
                WindowsStoreApplicationCatalog.LoadInstalledApplications,
                _loadCancellation.Token
            );

            if (_loadCancellation.IsCancellationRequested || IsDisposed)
                return;

            _filterTextBox.Enabled = true;
            _showSystemApplications.Enabled = true;
            _applicationList.Enabled = true;
            PopulateApplications();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (!IsDisposed)
                _statusLabel.Text = $"Windows Store discovery failed: {exception.Message}";
        }
        finally
        {
            if (!IsDisposed)
                _loadingOverlay.HideLoading();
        }
    }

    /// <summary>Rebuilds visible package applications when either filter changes.</summary>
    /// <param name="sender">The changed filter control.</param>
    /// <param name="e">The change event data.</param>
    private void FilterChanged(object? sender, EventArgs e) => PopulateApplications();

    /// <summary>Enables selection when exactly one package application is selected.</summary>
    /// <param name="sender">The application list.</param>
    /// <param name="e">The selection event data.</param>
    private void ApplicationSelectionChanged(object? sender, EventArgs e)
    {
        _selectButton.Enabled = SelectedApplication is not null;
    }

    /// <summary>Accepts the selected package application on double-click.</summary>
    /// <param name="sender">The application list.</param>
    /// <param name="e">The double-click event data.</param>
    private void ApplicationDoubleClicked(object? sender, EventArgs e)
    {
        if (SelectedApplication is not null)
            AcceptSelection();
    }

    /// <summary>Accepts the selected package application.</summary>
    /// <param name="sender">The Select button.</param>
    /// <param name="e">The click event data.</param>
    private void SelectClicked(object? sender, EventArgs e) => AcceptSelection();

    /// <summary>Rebuilds visible rows using text and Microsoft/system filters.</summary>
    private void PopulateApplications()
    {
        string filter = _filterTextBox.Text.Trim();
        int systemHiddenCount = 0;
        int textFilterHiddenCount = 0;
        _applicationList.BeginUpdate();

        try
        {
            _applicationList.Items.Clear();

            foreach (InstalledStoreApplication application in _applications)
            {
                if (application.IsMicrosoftOrSystem && !_showSystemApplications.Checked)
                {
                    systemHiddenCount++;
                    continue;
                }

                if (filter.Length > 0 &&
                    !application.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !application.PackageName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    textFilterHiddenCount++;
                    continue;
                }

                var row = new ListViewItem(application.DisplayName)
                {
                    Tag = application,
                    ImageKey = _icons.GetImageKey(application.ExecutablePath)
                };
                row.SubItems.Add(application.PackageName);
                row.SubItems.Add(application.ExecutablePath);
                _applicationList.Items.Add(row);
            }
        }
        finally
        {
            _applicationList.EndUpdate();
        }

        _selectButton.Enabled = false;
        int visibleCount = _applicationList.Items.Count;
        string status = visibleCount == 1
            ? "1 application shown."
            : $"{visibleCount} applications shown.";

        if (systemHiddenCount > 0)
        {
            status += systemHiddenCount == 1
                ? " 1 Microsoft/system application filtered out."
                : $" {systemHiddenCount} Microsoft/system applications filtered out.";
        }

        if (textFilterHiddenCount > 0)
        {
            status += textFilterHiddenCount == 1
                ? " 1 application does not match the text filter."
                : $" {textFilterHiddenCount} applications do not match the text filter.";
        }

        _statusLabel.Text = status;
    }

    /// <summary>Stores the selected application's launch and update-safe package identity fields.</summary>
    private void AcceptSelection()
    {
        if (SelectedApplication is not InstalledStoreApplication application)
            return;

        SelectedExecutablePath = application.ExecutablePath;
        SelectedAppUri = application.AppUri;
        SelectedPackageFamilyName = application.PackageFamilyName;
        SelectedPackageApplicationId = application.ApplicationId;
        SelectedPackageExecutable = application.ExecutableRelativePath;
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>Gets the currently selected package application.</summary>
    private InstalledStoreApplication? SelectedApplication =>
        _applicationList.SelectedItems.Count == 1
            ? _applicationList.SelectedItems[0].Tag as InstalledStoreApplication
            : null;
}
