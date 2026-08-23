using AppSupervisor.Configuration;
using AppSupervisor.Health;
using System.ComponentModel;

namespace AppSupervisor.ConfigurationUI;

/// <summary>Edits one detached startup macro action, including full-chord hotkey capture.</summary>
public sealed class StartupMacroActionEditorDialog : Form
{
    private readonly string _applicationPath;
    private readonly ComboBox _type = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Dock = DockStyle.Fill,
        DrawMode = DrawMode.OwnerDrawFixed,
        FormattingEnabled = true
    };
    private readonly NumericUpDown _delay = CreateNumeric(0, 86_400_000);
    private readonly HotkeyCaptureTextBox _hotkey = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _monitor = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Dock = DockStyle.Fill,
        FormattingEnabled = true
    };
    private readonly NumericUpDown _x = CreateNumeric(-100_000, 100_000);
    private readonly NumericUpDown _y = CreateNumeric(-100_000, 100_000);
    private readonly NumericUpDown _width = CreateNumeric(1, 65_535);
    private readonly NumericUpDown _height = CreateNumeric(1, 65_535);
    private readonly FlowLayoutPanel _readCurrentPanel = new()
    {
        AutoSize = true,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false,
        Margin = Padding.Empty
    };
    private readonly Button _readPosition = new() { Text = "Read current position", AutoSize = true };
    private readonly Button _readSize = new() { Text = "Read current size", AutoSize = true };
    private readonly TableLayoutPanel _layout;
    private readonly Dictionary<Control, Label> _labels = [];

    public StartupMacroActionEditorDialog(
        StartupMacroActionConfig configuration,
        string applicationPath)
    {
        _applicationPath = applicationPath;
        Text = "Startup macro action";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(560, 430);
        Size = new Size(640, 480);
        AutoScaleMode = AutoScaleMode.Dpi;

        _layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(14)
        };
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow("Action", _type);
        AddRow("Delay (ms)", _delay);
        AddRow("Hotkey", BuildHotkeyPanel());
        AddRow("Monitor", _monitor);
        AddRow("X", _x);
        AddRow("Y", _y);
        AddRow("Width", _width);
        AddRow("Height", _height);
        _readCurrentPanel.Controls.Add(_readPosition);
        _readCurrentPanel.Controls.Add(_readSize);
        _readPosition.Margin = Padding.Empty;
        _readSize.Margin = Padding.Empty;
        AddRow("Current window", _readCurrentPanel);

        var hint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Move coordinates are relative to the selected monitor's working area. Window actions do not activate the target window. Hotkeys are injected system-wide and may also be observed by the active application."
        };
        int hintRow = _layout.RowCount++;
        _layout.Controls.Add(hint, 0, hintRow);
        _layout.SetColumnSpan(hint, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10)
        };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var save = new Button { Text = "Save action", AutoSize = true };
        save.Click += SaveClicked;
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);

        Controls.Add(new Panel { Dock = DockStyle.Fill, AutoScroll = true, Controls = { _layout } });
        Controls.Add(buttons);
        AcceptButton = save;
        CancelButton = cancel;

        _type.DataSource = Enum.GetValues<StartupMacroActionType>();
        _type.ItemHeight = ConfigurationIconListRenderer.GetItemHeight(_type);
        _type.Format += (_, args) =>
        {
            if (args.ListItem is StartupMacroActionType type)
                args.Value = StartupMacroDisplay.ActionType(type);
        };
        _type.DrawItem += TypeDrawItem;
        _type.SelectedValueChanged += (_, _) => UpdateVisibleFields();
        _readPosition.Click += ReadCurrentPositionClicked;
        _readSize.Click += ReadCurrentSizeClicked;

        foreach (DisplayMonitorCatalog.MonitorChoice monitor in DisplayMonitorCatalog.Load())
            _monitor.Items.Add(monitor);
        _monitor.DisplayMember = nameof(DisplayMonitorCatalog.MonitorChoice.DisplayName);

        LoadConfiguration(configuration);
    }

    public StartupMacroActionConfig? Result { get; private set; }

    private void TypeDrawItem(object? sender, DrawItemEventArgs e)
    {
        StartupMacroActionType? type = e.Index >= 0 && e.Index < _type.Items.Count
            ? _type.Items[e.Index] as StartupMacroActionType?
            : _type.SelectedItem as StartupMacroActionType?;
        ConfigurationIconListRenderer.DrawItem(
            e,
            _type.Font,
            StartupMacroDisplay.ActionType(type),
            (graphics, bounds, color, _) =>
                ConfigurationItemIconRenderer.DrawStartupMacro(graphics, bounds, type, color)
        );
    }

    private Control BuildHotkeyPanel()
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
        _hotkey.Margin = Padding.Empty;
        var clear = new Button
        {
            Text = "Clear",
            AutoSize = true,
            Margin = new Padding(8, 0, 0, 0)
        };
        clear.Click += (_, _) => _hotkey.CapturedKeys = [];
        panel.Controls.Add(_hotkey, 0, 0);
        panel.Controls.Add(clear, 1, 0);
        return panel;
    }

    private void LoadConfiguration(StartupMacroActionConfig configuration)
    {
        _type.SelectedItem = configuration.Type ?? StartupMacroActionType.Delay;
        _delay.Value = Math.Clamp(configuration.DelayMilliseconds ?? 2_000, 0, 86_400_000);
        _hotkey.CapturedKeys = configuration.Keys ?? [];
        _x.Value = Math.Clamp(configuration.X ?? 0, -100_000, 100_000);
        _y.Value = Math.Clamp(configuration.Y ?? 0, -100_000, 100_000);
        _width.Value = Math.Clamp(configuration.Width ?? 800, 1, 65_535);
        _height.Value = Math.Clamp(configuration.Height ?? 600, 1, 65_535);

        string monitor = configuration.Monitor ?? "";
        DisplayMonitorCatalog.MonitorChoice? option =
            _monitor.Items.Cast<DisplayMonitorCatalog.MonitorChoice>().FirstOrDefault(candidate =>
            string.Equals(candidate.DeviceName, monitor, StringComparison.OrdinalIgnoreCase));

        if (option is null && string.IsNullOrWhiteSpace(monitor))
        {
            option = _monitor.Items.Cast<DisplayMonitorCatalog.MonitorChoice>()
                .FirstOrDefault(candidate => candidate.Primary);
        }

        if (option is null)
        {
            option = new DisplayMonitorCatalog.MonitorChoice(
                monitor,
                $"{monitor} (disconnected)",
                Rectangle.Empty,
                Primary: false
            );
            _monitor.Items.Add(option);
        }
        _monitor.SelectedItem = option;
        UpdateVisibleFields();
    }

    private void SaveClicked(object? sender, EventArgs e)
    {
        if (_type.SelectedItem is not StartupMacroActionType type)
            return;

        if (type == StartupMacroActionType.Hotkey && _hotkey.CapturedKeys.Count == 0)
        {
            MessageBox.Show(this, "Capture at least one hotkey.", "Hotkey required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Result = new StartupMacroActionConfig { Type = type };

        switch (type)
        {
            case StartupMacroActionType.Delay:
                Result.DelayMilliseconds = Decimal.ToInt32(_delay.Value);
                break;
            case StartupMacroActionType.Hotkey:
                Result.Keys = [.. _hotkey.CapturedKeys];
                break;
            case StartupMacroActionType.MoveWindow:
                Result.Monitor =
                    (_monitor.SelectedItem as DisplayMonitorCatalog.MonitorChoice)?.DeviceName ?? "";
                Result.X = Decimal.ToInt32(_x.Value);
                Result.Y = Decimal.ToInt32(_y.Value);
                break;
            case StartupMacroActionType.ResizeWindow:
                Result.Width = Decimal.ToInt32(_width.Value);
                Result.Height = Decimal.ToInt32(_height.Value);
                break;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private void UpdateVisibleFields()
    {
        StartupMacroActionType? type = _type.SelectedItem as StartupMacroActionType?;
        SetVisible(_delay, type == StartupMacroActionType.Delay);
        SetVisible(_hotkey.Parent!, type == StartupMacroActionType.Hotkey);
        SetVisible(_monitor, type == StartupMacroActionType.MoveWindow);
        SetVisible(_x, type == StartupMacroActionType.MoveWindow);
        SetVisible(_y, type == StartupMacroActionType.MoveWindow);
        SetVisible(_width, type == StartupMacroActionType.ResizeWindow);
        SetVisible(_height, type == StartupMacroActionType.ResizeWindow);
        SetVisible(
            _readCurrentPanel,
            type is StartupMacroActionType.MoveWindow or StartupMacroActionType.ResizeWindow
        );
        _readPosition.Visible = type == StartupMacroActionType.MoveWindow;
        _readSize.Visible = type == StartupMacroActionType.ResizeWindow;
    }

    private void ReadCurrentPositionClicked(object? sender, EventArgs e)
    {
        if (!TryReadCurrentBounds(out Rectangle bounds))
            return;

        if (_monitor.SelectedItem is not DisplayMonitorCatalog.MonitorChoice monitor ||
            monitor.WorkingArea == Rectangle.Empty)
        {
            ShowReadError("Select a connected monitor first.");
            return;
        }

        Point relative = StartupMacroWindowActions.ToMonitorRelativePosition(
            bounds,
            monitor.WorkingArea
        );
        SetNumericValue(_x, relative.X, "X coordinate");
        SetNumericValue(_y, relative.Y, "Y coordinate");
    }

    private void ReadCurrentSizeClicked(object? sender, EventArgs e)
    {
        if (!TryReadCurrentBounds(out Rectangle bounds))
            return;

        SetNumericValue(_width, bounds.Width, "window width");
        SetNumericValue(_height, bounds.Height, "window height");
    }

    private bool TryReadCurrentBounds(out Rectangle bounds)
    {
        IReadOnlySet<int> processIds = ProcessPathDiscovery.FindRunningProcessIds(
            _applicationPath,
            useSharedCache: false
        );
        StartupMacroWindowActions.ExecutionResult result =
            StartupMacroWindowActions.ReadCurrentWindowBounds(processIds, out bounds);

        if (result.Status == StartupMacroWindowActions.ExecutionStatus.Succeeded)
            return true;

        ShowReadError(result.Detail);
        return false;
    }

    private void SetNumericValue(NumericUpDown control, int value, string description)
    {
        if (value < control.Minimum || value > control.Maximum)
        {
            ShowReadError(
                $"The current {description} ({value}) is outside the supported range " +
                $"{control.Minimum} to {control.Maximum}."
            );
            return;
        }

        control.Value = value;
    }

    private void ShowReadError(string message)
    {
        MessageBox.Show(
            this,
            message,
            "Current window could not be read",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning
        );
    }

    private void SetVisible(Control control, bool visible)
    {
        control.Visible = visible;
        if (_labels.TryGetValue(control, out Label? label))
            label.Visible = visible;
    }

    private void AddRow(string labelText, Control control)
    {
        int row = _layout.RowCount++;
        var label = new Label
        {
            Text = labelText,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 12, 6)
        };
        control.Margin = new Padding(0, 4, 0, 7);
        _layout.Controls.Add(label, 0, row);
        _layout.Controls.Add(control, 1, row);
        _labels[control] = label;
    }

    private static NumericUpDown CreateNumeric(decimal minimum, decimal maximum) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        Width = 160,
        ThousandsSeparator = true
    };

}

/// <summary>Captures every key held in one chord until all keys are released.</summary>
internal sealed class HotkeyCaptureTextBox : TextBox
{
    private readonly HashSet<Keys> _pressed = [];
    private readonly List<Keys> _capture = [];
    private List<string> _capturedKeys = [];

    public HotkeyCaptureTextBox()
    {
        ReadOnly = true;
        ShortcutsEnabled = false;
        Text = "Click here, then hold the complete shortcut";
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<string> CapturedKeys
    {
        get => _capturedKeys;
        set
        {
            _capturedKeys = [.. value];
            Text = _capturedKeys.Count == 0
                ? "Click here, then hold the complete shortcut"
                : StartupMacroWindowActions.FormatHotkey(_capturedKeys);
        }
    }

    protected override void OnPreviewKeyDown(PreviewKeyDownEventArgs e)
    {
        e.IsInputKey = true;
        base.OnPreviewKeyDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;

        if (_pressed.Count == 0)
            _capture.Clear();

        if (_pressed.Add(e.KeyCode) && !_capture.Contains(e.KeyCode))
            _capture.Add(e.KeyCode);

        Text = StartupMacroWindowActions.FormatHotkey(_capture.Select(key => key.ToString()));
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;
        _pressed.Remove(e.KeyCode);

        if (_pressed.Count == 0 && _capture.Count > 0)
            CapturedKeys = _capture.Select(key => key.ToString()).ToList();

        base.OnKeyUp(e);
    }
}
