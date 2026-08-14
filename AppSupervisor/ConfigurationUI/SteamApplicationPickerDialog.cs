using AppSupervisor.Steam;

namespace AppSupervisor.ConfigurationUI;

/// <summary>Lets the user choose an installed Steam item and its real monitored executable.</summary>
internal sealed class SteamApplicationPickerDialog : Form
{
    private IReadOnlyList<InstalledSteamItem> _items = [];
    private readonly TextBox _filterTextBox;
    private readonly ListView _itemList;
    private readonly ComboBox _executableSelector;
    private readonly Label _statusLabel;
    private readonly Button _selectButton;
    private readonly CancellationTokenSource _loadCancellation = new();
    private readonly ExecutableIconList _icons;
    private CancellationTokenSource? _candidateCancellation;

    /// <summary>Creates the installed-item picker from every locally registered Steam library.</summary>
    public SteamApplicationPickerDialog()
    {
        Text = "Choose installed Steam item";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 520);
        Size = new Size(940, 650);
        AutoScaleMode = AutoScaleMode.Dpi;
        _icons = new ExecutableIconList(DeviceDpi);

        var filterPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(10)
        };
        filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filterPanel.Controls.Add(new Label
        {
            Text = "Filter:",
            AutoSize = true,
            Margin = new Padding(0, 6, 8, 0)
        }, 0, 0);
        _filterTextBox = new TextBox { Dock = DockStyle.Fill, Enabled = false };
        _filterTextBox.TextChanged += FilterTextChanged;
        filterPanel.Controls.Add(_filterTextBox, 1, 0);

        _itemList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            Enabled = false,
            SmallImageList = _icons.Images
        };
        _itemList.Columns.Add("Steam item", 300);
        _itemList.Columns.Add("App ID", 100);
        _itemList.Columns.Add("Installation directory", 480);
        _itemList.SelectedIndexChanged += ItemSelectionChanged;

        var executablePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(10, 8, 10, 4)
        };
        executablePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        executablePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        executablePanel.Controls.Add(new Label
        {
            Text = "Monitored executable:",
            AutoSize = true,
            Margin = new Padding(0, 7, 8, 0)
        }, 0, 0);
        _executableSelector = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = Math.Max(Font.Height + 2, _icons.Images.ImageSize.Height + 2)
        };
        _executableSelector.DrawItem += ExecutableSelectorDrawItem;
        _executableSelector.SelectedIndexChanged += ExecutableSelectionChanged;
        executablePanel.Controls.Add(_executableSelector, 1, 0);
        _statusLabel = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 6, 0, 0),
            Text = "Discovering installed Steam items..."
        };
        executablePanel.Controls.Add(_statusLabel, 0, 1);
        executablePanel.SetColumnSpan(_statusLabel, 2);

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

        Controls.Add(_itemList);
        Controls.Add(filterPanel);
        Controls.Add(executablePanel);
        Controls.Add(buttons);
        AcceptButton = _selectButton;
        CancelButton = cancelButton;
        Shown += DialogShown;
    }

    /// <summary>Gets the selected real executable path used for monitoring.</summary>
    public string? SelectedExecutablePath { get; private set; }

    /// <summary>Gets the selected Steam rungameid app URI.</summary>
    public string? SelectedAppUri { get; private set; }

    /// <summary>Cancels executable discovery and releases dialog controls.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loadCancellation.Cancel();
            _loadCancellation.Dispose();
            _candidateCancellation?.Cancel();
            _candidateCancellation?.Dispose();
            _icons.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Discovers installed Steam libraries away from the UI thread after the picker is visible.</summary>
    /// <param name="sender">The displayed picker.</param>
    /// <param name="e">The shown event data.</param>
    private async void DialogShown(object? sender, EventArgs e)
    {
        Shown -= DialogShown;

        try
        {
            _items = await Task.Run(
                SteamLibraryCatalog.LoadInstalledItems,
                _loadCancellation.Token
            );

            if (_loadCancellation.IsCancellationRequested || IsDisposed)
                return;

            _filterTextBox.Enabled = true;
            _itemList.Enabled = true;
            PopulateItems();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (!IsDisposed)
                _statusLabel.Text = $"Steam library discovery failed: {exception.Message}";
        }
    }

    /// <summary>Rebuilds installed-item rows as the user types.</summary>
    /// <param name="sender">The filter text box.</param>
    /// <param name="e">The change event data.</param>
    private void FilterTextChanged(object? sender, EventArgs e) => PopulateItems();

    /// <summary>Discovers executable candidates for the selected item without blocking the UI.</summary>
    /// <param name="sender">The installed-item list.</param>
    /// <param name="e">The selection event data.</param>
    private async void ItemSelectionChanged(object? sender, EventArgs e)
    {
        _candidateCancellation?.Cancel();
        _candidateCancellation?.Dispose();
        _candidateCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _candidateCancellation.Token;
        _executableSelector.Items.Clear();
        _selectButton.Enabled = false;

        if (SelectedItem is not InstalledSteamItem item)
        {
            _statusLabel.Text = "Select a Steam item.";
            return;
        }

        _statusLabel.Text = "Scanning the selected installation for executables...";

        try
        {
            IReadOnlyList<string> candidates = await Task.Run(
                () => SteamLibraryCatalog.FindExecutableCandidates(item, cancellationToken),
                cancellationToken
            );

            if (cancellationToken.IsCancellationRequested || IsDisposed)
                return;

            _executableSelector.Items.AddRange(candidates.Cast<object>().ToArray());

            if (candidates.Count == 0)
            {
                _statusLabel.Text = "No executable was found in this item's installation directory.";
                return;
            }

            _executableSelector.SelectedIndex = 0;
            _statusLabel.Text = candidates.Count == 1
                ? "One executable found."
                : $"{candidates.Count} executables found; the likely main executable is selected.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _statusLabel.Text = $"Executable discovery failed: {exception.Message}";
        }
    }

    /// <summary>Enables selection after a real executable candidate is chosen.</summary>
    /// <param name="sender">The executable selector.</param>
    /// <param name="e">The selection event data.</param>
    private void ExecutableSelectionChanged(object? sender, EventArgs e)
    {
        _selectButton.Enabled = SelectedItem is not null &&
            _executableSelector.SelectedItem is string;
    }

    /// <summary>Draws executable candidates with the exact file icon and a compact path label.</summary>
    private void ExecutableSelectorDrawItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();

        if (e.Index < 0 || e.Index >= _executableSelector.Items.Count ||
            _executableSelector.Items[e.Index] is not string path)
        {
            return;
        }

        int iconSize = Math.Min(_icons.Images.ImageSize.Width, e.Bounds.Height - 2);
        var iconBounds = new Rectangle(
            e.Bounds.Left + 2,
            e.Bounds.Top + (e.Bounds.Height - iconSize) / 2,
            iconSize,
            iconSize
        );
        _icons.Draw(e.Graphics, iconBounds, path);
        var textBounds = new Rectangle(
            iconBounds.Right + 4,
            e.Bounds.Top,
            Math.Max(0, e.Bounds.Right - iconBounds.Right - 6),
            e.Bounds.Height
        );
        TextRenderer.DrawText(
            e.Graphics,
            path,
            _executableSelector.Font,
            textBounds,
            e.ForeColor,
            TextFormatFlags.Left |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix
        );
        e.DrawFocusRectangle();
    }

    /// <summary>Stores the chosen executable and Steam app URI.</summary>
    /// <param name="sender">The Select button.</param>
    /// <param name="e">The click event data.</param>
    private void SelectClicked(object? sender, EventArgs e)
    {
        if (SelectedItem is not InstalledSteamItem item ||
            _executableSelector.SelectedItem is not string executablePath)
        {
            return;
        }

        SelectedExecutablePath = executablePath;
        SelectedAppUri = item.AppUri;
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>Rebuilds visible installed Steam items using the current text filter.</summary>
    private void PopulateItems()
    {
        string filter = _filterTextBox.Text.Trim();
        _itemList.BeginUpdate();

        try
        {
            _itemList.Items.Clear();

            foreach (InstalledSteamItem item in _items)
            {
                if (filter.Length > 0 &&
                    !item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !item.AppId.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var row = new ListViewItem(item.Name)
                {
                    Tag = item,
                    ImageKey = _icons.GetImageKey(item.IconExecutablePath)
                };
                row.SubItems.Add(item.AppId.ToString());
                row.SubItems.Add(item.InstallDirectory);
                _itemList.Items.Add(row);
            }
        }
        finally
        {
            _itemList.EndUpdate();
        }

        _statusLabel.Text = _items.Count == 0
            ? "No installed Steam items were found."
            : "Select a Steam item.";
    }

    /// <summary>Gets the one currently selected installed item.</summary>
    private InstalledSteamItem? SelectedItem =>
        _itemList.SelectedItems.Count == 1
            ? _itemList.SelectedItems[0].Tag as InstalledSteamItem
            : null;
}
