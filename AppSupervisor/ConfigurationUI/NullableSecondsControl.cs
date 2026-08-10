namespace AppSupervisor.ConfigurationUI;

/// <summary>
/// Edits an optional non-negative seconds value while clearly exposing use of the built-in default.
/// </summary>
public sealed class NullableSecondsControl : UserControl
{
    private readonly CheckBox _overrideCheckBox;
    private readonly NumericUpDown _seconds;
    private bool _loading;

    /// <summary>Creates a default/override selector and bounded seconds input.</summary>
    public NullableSecondsControl()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var layout = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty,
            WrapContents = false
        };
        _overrideCheckBox = new CheckBox
        {
            AutoSize = true,
            Text = "Override default",
            Margin = new Padding(0, 5, 8, 3)
        };
        _seconds = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 86400,
            Width = 90,
            Enabled = false
        };
        var suffix = new Label
        {
            AutoSize = true,
            Text = "seconds",
            Margin = new Padding(4, 7, 0, 0)
        };
        _overrideCheckBox.CheckedChanged += OnValueChanged;
        _seconds.ValueChanged += OnValueChanged;
        layout.Controls.Add(_overrideCheckBox);
        layout.Controls.Add(_seconds);
        layout.Controls.Add(suffix);
        Controls.Add(layout);
    }

    /// <summary>Occurs when the user changes default/override selection or seconds.</summary>
    public event EventHandler? ValueChanged;

    /// <summary>Gets or sets the optional seconds value.</summary>
    [System.ComponentModel.DesignerSerializationVisibilityAttribute(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int? Value
    {
        get => _overrideCheckBox.Checked ? Decimal.ToInt32(_seconds.Value) : null;
        set
        {
            _loading = true;

            try
            {
                _overrideCheckBox.Checked = value is not null;
                _seconds.Enabled = value is not null;
                _seconds.Value = Math.Clamp(
                    value ?? 0,
                    Decimal.ToInt32(_seconds.Minimum),
                    Decimal.ToInt32(_seconds.Maximum)
                );
            }
            finally
            {
                _loading = false;
            }
        }
    }

    /// <summary>Synchronizes numeric input availability and forwards a user-originated change.</summary>
    /// <param name="sender">The changed child control.</param>
    /// <param name="e">The change event data.</param>
    private void OnValueChanged(object? sender, EventArgs e)
    {
        _seconds.Enabled = _overrideCheckBox.Checked;

        if (!_loading)
            ValueChanged?.Invoke(this, EventArgs.Empty);
    }
}
