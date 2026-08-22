namespace AppSupervisor.ConfigurationUI;

/// <summary>Lets the user choose current-avatar OSC parameter leaves while marking configured names.</summary>
internal sealed class VrcOscParameterPickerDialog : Form
{
    private readonly CheckedListBox _parameterList;

    /// <summary>Creates a picker containing every available parameter and checks configured entries.</summary>
    internal VrcOscParameterPickerDialog(
        IReadOnlyList<string> availableParameters,
        IReadOnlyCollection<string> configuredParameters)
    {
        ArgumentNullException.ThrowIfNull(availableParameters);
        ArgumentNullException.ThrowIfNull(configuredParameters);

        Text = "Choose VRChat OSC parameters";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(500, 460);
        Size = new Size(620, 680);
        AutoScaleMode = AutoScaleMode.Dpi;

        var configured = configuredParameters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _parameterList = new CheckedListBox
        {
            CheckOnClick = true,
            Dock = DockStyle.Fill,
            HorizontalScrollbar = true,
            IntegralHeight = false,
            Sorted = true
        };

        foreach (string parameter in availableParameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _parameterList.Items.Add(parameter, configured.Contains(parameter));
        }

        var guidance = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(10),
            Text = "Checked parameters are already in the list. Change the check marks, then use the checked selection."
        };
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10)
        };
        var cancelButton = new Button
        {
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            Text = "Cancel"
        };
        var applyButton = new Button
        {
            AutoSize = true,
            Text = "Use checked"
        };
        applyButton.Click += ApplyClicked;
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(applyButton);

        Controls.Add(_parameterList);
        Controls.Add(guidance);
        Controls.Add(buttons);
        AcceptButton = applyButton;
        CancelButton = cancelButton;
    }

    /// <summary>Gets the available parameters that were checked when the dialog was accepted.</summary>
    internal IReadOnlyList<string> SelectedParameters { get; private set; } = [];

    private void ApplyClicked(object? sender, EventArgs e)
    {
        SelectedParameters = _parameterList.CheckedItems
            .Cast<string>()
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        DialogResult = DialogResult.OK;
        Close();
    }
}
