using System.Diagnostics;

namespace AppSupervisor.ConfigurationUI;

/// <summary>
/// Lets the user select a unique running executable while hiding standard Microsoft and Windows entries by default.
/// </summary>
public sealed class RunningProcessPickerDialog : Form
{
    private static readonly HashSet<string> InaccessibleWindowsProcessNames = new(
        ["Idle.exe", "System.exe", "Registry.exe", "Secure System.exe", "Memory Compression.exe"],
        StringComparer.OrdinalIgnoreCase
    );

    private readonly TextBox _filterTextBox;
    private readonly CheckBox _showMicrosoftProcesses;
    private readonly ListView _processList;
    private readonly Label _statusLabel;
    private readonly Button _refreshButton;
    private readonly Button _selectButton;
    private readonly ExecutableIconList _icons;
    private readonly CancellationTokenSource _refreshCancellation = new();
    private List<ProcessRow> _allRows = [];
    private bool _refreshRunning;

    /// <summary>Creates the searchable, de-duplicated running-process picker and refreshes its snapshot.</summary>
    public RunningProcessPickerDialog()
    {
        Text = "Choose running application";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 450);
        Size = new Size(920, 560);
        AutoScaleMode = AutoScaleMode.Dpi;
        _icons = new ExecutableIconList(DeviceDpi);

        var topPanel = new TableLayoutPanel
        {
            ColumnCount = 4,
            RowCount = 1,
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10)
        };
        topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        topPanel.Controls.Add(new Label
        {
            Text = "Filter:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 8, 0)
        }, 0, 0);
        _filterTextBox = new TextBox { Dock = DockStyle.Fill, Enabled = false };
        _filterTextBox.TextChanged += FilterTextChanged;
        topPanel.Controls.Add(_filterTextBox, 1, 0);
        _refreshButton = new Button
        {
            Text = "Refresh",
            AutoSize = true,
            Margin = new Padding(8, 0, 8, 0)
        };
        _refreshButton.Click += RefreshClicked;
        topPanel.Controls.Add(_refreshButton, 2, 0);
        _showMicrosoftProcesses = new CheckBox
        {
            Text = "Show Microsoft/Windows applications",
            AutoSize = true,
            Enabled = false,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 5, 0, 0)
        };
        _showMicrosoftProcesses.CheckedChanged += ShowMicrosoftProcessesChanged;
        topPanel.Controls.Add(_showMicrosoftProcesses, 3, 0);

        _processList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            Sorting = SortOrder.Ascending,
            SmallImageList = _icons.Images
        };
        _processList.Columns.Add("Application", 220);
        _processList.Columns.Add("Executable path", 650);
        _processList.SelectedIndexChanged += ProcessSelectionChanged;
        _processList.DoubleClick += ProcessDoubleClicked;

        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(10, 6, 10, 2),
            Text = "Discovering running applications..."
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
        _selectButton = new Button
        {
            Text = "Select",
            AutoSize = true,
            Enabled = false
        };
        _selectButton.Click += SelectClicked;
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(_selectButton);

        Controls.Add(_processList);
        Controls.Add(topPanel);
        Controls.Add(_statusLabel);
        Controls.Add(buttons);
        AcceptButton = _selectButton;
        CancelButton = cancelButton;
        Shown += DialogShown;
    }

    /// <summary>Gets the selected executable filename, including its .exe extension.</summary>
    public string? SelectedProcessName { get; private set; }

    /// <summary>Gets the selected process fully qualified executable path when it was accessible.</summary>
    public string? SelectedExecutablePath { get; private set; }

    /// <summary>Determines whether a process is a standard Microsoft or Windows entry hidden by default.</summary>
    /// <param name="processName">The executable filename.</param>
    /// <param name="executablePath">The accessible full executable path, if available.</param>
    /// <param name="companyName">The executable version-resource company name, if available.</param>
    /// <param name="windowsDirectory">The current Windows installation directory.</param>
    /// <returns><see langword="true"/> when the process should be hidden by the default filter.</returns>
    internal static bool IsStandardMicrosoftProcess(
        string processName,
        string? executablePath,
        string? companyName,
        string windowsDirectory)
    {
        if (InaccessibleWindowsProcessNames.Contains(processName))
            return true;

        if (!string.IsNullOrWhiteSpace(companyName) &&
            companyName.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(executablePath) ||
            string.IsNullOrWhiteSpace(windowsDirectory))
        {
            return false;
        }

        try
        {
            string fullExecutablePath = Path.GetFullPath(executablePath);
            string fullWindowsDirectory = Path.GetFullPath(windowsDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullExecutablePath.StartsWith(
                fullWindowsDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            );
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Removes repeated instances while retaining distinct executables that share a filename.</summary>
    /// <param name="rows">The raw per-process snapshot rows.</param>
    /// <returns>One sorted row for each executable path or inaccessible process name.</returns>
    internal static IReadOnlyList<ProcessRow> RemoveDuplicateProcesses(
        IEnumerable<ProcessRow> rows)
    {
        return rows
            .GroupBy(
                row => string.IsNullOrWhiteSpace(row.Path)
                    ? $"name:{row.Name}"
                    : $"path:{row.Path}",
                StringComparer.OrdinalIgnoreCase
            )
            .Select(group => group.First())
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Refreshes the process snapshot after the user presses Refresh.</summary>
    /// <param name="sender">The Refresh button.</param>
    /// <param name="e">The click event data.</param>
    private async void RefreshClicked(object? sender, EventArgs e)
        => await RefreshProcessesAsync();

    /// <summary>Starts initial process discovery only after the picker is visible.</summary>
    /// <param name="sender">The displayed picker.</param>
    /// <param name="e">The shown event data.</param>
    private async void DialogShown(object? sender, EventArgs e)
    {
        Shown -= DialogShown;
        await RefreshProcessesAsync();
    }

    /// <summary>Reapplies the text filter as the user types.</summary>
    /// <param name="sender">The filter text box.</param>
    /// <param name="e">The text-change event data.</param>
    private void FilterTextChanged(object? sender, EventArgs e) => PopulateList();

    /// <summary>Reapplies the process list when hidden Microsoft entries are requested.</summary>
    /// <param name="sender">The Microsoft-process visibility check box.</param>
    /// <param name="e">The check-state event data.</param>
    private void ShowMicrosoftProcessesChanged(object? sender, EventArgs e) => PopulateList();

    /// <summary>Enables selection only when exactly one process row is selected.</summary>
    /// <param name="sender">The process list.</param>
    /// <param name="e">The selection-change event data.</param>
    private void ProcessSelectionChanged(object? sender, EventArgs e)
    {
        _selectButton.Enabled = _processList.SelectedItems.Count == 1;
    }

    /// <summary>Accepts an application immediately when its row is double-clicked.</summary>
    /// <param name="sender">The application list.</param>
    /// <param name="e">The double-click event data.</param>
    private void ProcessDoubleClicked(object? sender, EventArgs e)
    {
        if (_processList.SelectedItems.Count == 1)
            AcceptSelection();
    }

    /// <summary>Accepts the currently selected application row.</summary>
    /// <param name="sender">The Select button.</param>
    /// <param name="e">The click event data.</param>
    private void SelectClicked(object? sender, EventArgs e) => AcceptSelection();

    /// <summary>Captures process metadata on a worker and applies the completed snapshot on the UI thread.</summary>
    private async Task RefreshProcessesAsync()
    {
        if (_refreshRunning)
            return;

        _refreshRunning = true;
        _refreshButton.Enabled = false;
        _refreshButton.Text = "Refreshing...";
        _statusLabel.Text = "Discovering running applications...";

        try
        {
            IReadOnlyList<ProcessRow> rows = await Task.Run(
                CaptureProcesses,
                _refreshCancellation.Token
            );

            if (_refreshCancellation.IsCancellationRequested || IsDisposed)
                return;

            _allRows = rows.ToList();
            _filterTextBox.Enabled = true;
            _showMicrosoftProcesses.Enabled = true;
            PopulateList();
            _statusLabel.Text = $"{_allRows.Count} unique running application(s) discovered.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (!IsDisposed)
                _statusLabel.Text = $"Running application discovery failed: {exception.Message}";
        }
        finally
        {
            _refreshRunning = false;

            if (!IsDisposed)
            {
                _refreshButton.Enabled = true;
                _refreshButton.Text = "Refresh";
            }
        }
    }

    /// <summary>Reads one deduplicated process snapshot without accessing WinForms controls.</summary>
    /// <returns>The current unique process rows with publisher filtering metadata.</returns>
    private static IReadOnlyList<ProcessRow> CaptureProcesses()
    {
        var rows = new List<ProcessRow>();
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                string? path = process.MainModule?.FileName;
                string executableName = path is null
                    ? process.ProcessName + ".exe"
                    : Path.GetFileName(path);
                rows.Add(new ProcessRow(
                    executableName,
                    path,
                    IsStandardMicrosoftProcess(
                        executableName,
                        path,
                        TryReadCompanyName(path),
                        windowsDirectory
                    )
                ));
            }
            catch
            {
                string executableName = process.ProcessName + ".exe";
                rows.Add(new ProcessRow(
                    executableName,
                    null,
                    IsStandardMicrosoftProcess(
                        executableName,
                        executablePath: null,
                        companyName: null,
                        windowsDirectory
                    )
                ));
            }
            finally
            {
                process.Dispose();
            }
        }

        return RemoveDuplicateProcesses(rows);
    }

    /// <summary>Cancels pending process discovery when the picker closes.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshCancellation.Cancel();
            _refreshCancellation.Dispose();
            _icons.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Reads an executable's version-resource company name without exposing inspection failures.</summary>
    /// <param name="executablePath">The executable path to inspect.</param>
    /// <returns>The company name, or <see langword="null"/> when unavailable.</returns>
    private static string? TryReadCompanyName(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return null;

        try
        {
            return FileVersionInfo.GetVersionInfo(executablePath).CompanyName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Rebuilds visible rows using the Microsoft/Windows and case-insensitive text filters.</summary>
    private void PopulateList()
    {
        string filter = _filterTextBox.Text.Trim();
        _processList.BeginUpdate();

        try
        {
            _processList.Items.Clear();

            foreach (ProcessRow row in _allRows)
            {
                if (row.IsStandardMicrosoftProcess && !_showMicrosoftProcesses.Checked)
                    continue;

                if (filter.Length > 0 &&
                    !row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !(row.Path?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    continue;
                }

                var item = new ListViewItem(row.Name)
                {
                    Tag = row,
                    ImageKey = _icons.GetImageKey(row.Path)
                };
                item.SubItems.Add(row.Path ?? "Unavailable");
                _processList.Items.Add(item);
            }
        }
        finally
        {
            _processList.EndUpdate();
        }

        _selectButton.Enabled = false;
    }

    /// <summary>Stores the selected process name and closes the dialog with an OK result.</summary>
    private void AcceptSelection()
    {
        if (_processList.SelectedItems.Count != 1 ||
            _processList.SelectedItems[0].Tag is not ProcessRow row)
        {
            return;
        }

        SelectedProcessName = row.Name;
        SelectedExecutablePath = row.Path;
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>Stores one unique running-executable snapshot and its default-filter classification.</summary>
    internal sealed record ProcessRow(
        string Name,
        string? Path,
        bool IsStandardMicrosoftProcess);
}
