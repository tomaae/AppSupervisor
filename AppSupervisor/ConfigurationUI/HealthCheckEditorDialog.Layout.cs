namespace AppSupervisor.ConfigurationUI;

/// <summary>
/// Provides display formatting and full-width explanatory rows for the health-check editor.
/// </summary>
public sealed partial class HealthCheckEditorDialog
{
    /// <summary>Adds an unlabeled control that spans both settings-table columns.</summary>
    /// <param name="layout">The target settings table.</param>
    /// <param name="control">The full-width control.</param>
    private static void AddSpanningRow(TableLayoutPanel layout, Control control)
    {
        int row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Margin = new Padding(0, 3, 0, 6);
        layout.Controls.Add(control, 0, row);
        layout.SetColumnSpan(control, 2);
    }

    /// <summary>Formats health-check type values using user-facing names.</summary>
    /// <param name="sender">The type combo box.</param>
    /// <param name="e">The formatting event data.</param>
    private void TypeComboBoxFormat(object? sender, ListControlConvertEventArgs e)
    {
        if (e.ListItem is HealthCheckType type)
        {
            e.Value = type == HealthCheckType.Vrcosc
                ? "VRChat OSCQuery"
                : "Listener";
        }
    }

    /// <summary>Formats listener transports using their conventional uppercase abbreviations.</summary>
    /// <param name="sender">The protocol combo box.</param>
    /// <param name="e">The formatting event data.</param>
    private void ProtocolComboBoxFormat(object? sender, ListControlConvertEventArgs e)
    {
        if (e.ListItem is ListenerProtocol protocol)
            e.Value = protocol.ToString().ToUpperInvariant();
    }
}
