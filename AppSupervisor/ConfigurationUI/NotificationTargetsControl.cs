using AppSupervisor.Notifications;

namespace AppSupervisor.ConfigurationUI;

/// <summary>
/// Presents the three notification targets as independent, easy-to-scan check boxes.
/// </summary>
public sealed class NotificationTargetsControl : UserControl
{
    private readonly Dictionary<NotificationTarget, CheckBox> _checkBoxes = [];
    private bool _loading;

    /// <summary>Creates notification target check boxes in their stable configuration order.</summary>
    public NotificationTargetsControl()
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

        AddTarget(layout, NotificationTarget.Popup, "Popup dialog");
        AddTarget(layout, NotificationTarget.Windows, "Windows");
        AddTarget(layout, NotificationTarget.XsOverlay, "XSOverlay");
        Controls.Add(layout);
    }

    /// <summary>Occurs when the user changes any notification destination.</summary>
    public event EventHandler? TargetsChanged;

    /// <summary>Gets the currently selected targets in stable enum order.</summary>
    public IReadOnlyList<NotificationTarget> SelectedTargets => _checkBoxes
        .Where(pair => pair.Value.Checked)
        .Select(pair => pair.Key)
        .OrderBy(target => target)
        .ToArray();

    /// <summary>Loads target selections without raising a user-change event.</summary>
    /// <param name="targets">The targets that should be checked.</param>
    public void LoadTargets(IEnumerable<NotificationTarget> targets)
    {
        var selected = targets.ToHashSet();
        _loading = true;

        try
        {
            foreach ((NotificationTarget target, CheckBox checkBox) in _checkBoxes)
                checkBox.Checked = selected.Contains(target);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Adds one labeled target check box and wires its change notification.</summary>
    /// <param name="layout">The horizontal target layout.</param>
    /// <param name="target">The target represented by the check box.</param>
    /// <param name="text">The user-facing target label.</param>
    private void AddTarget(
        FlowLayoutPanel layout,
        NotificationTarget target,
        string text)
    {
        var checkBox = new CheckBox
        {
            AutoSize = true,
            Text = text,
            Margin = new Padding(0, 3, 16, 3)
        };
        checkBox.CheckedChanged += OnCheckedChanged;
        _checkBoxes.Add(target, checkBox);
        layout.Controls.Add(checkBox);
    }

    /// <summary>Forwards a target check-box change when it originated from the user.</summary>
    /// <param name="sender">The changed target check box.</param>
    /// <param name="e">The check-state event data.</param>
    private void OnCheckedChanged(object? sender, EventArgs e)
    {
        if (!_loading)
            TargetsChanged?.Invoke(this, EventArgs.Empty);
    }
}
