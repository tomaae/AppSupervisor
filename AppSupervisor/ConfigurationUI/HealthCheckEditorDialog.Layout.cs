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
            e.Value = HealthCheckDisplay.Type(type);
    }

    /// <summary>Draws a health-check type with its network-listener or OSCQuery pictogram.</summary>
    private void TypeComboBoxDrawItem(object? sender, DrawItemEventArgs e)
    {
        HealthCheckType? type = e.Index >= 0 && e.Index < _typeComboBox.Items.Count
            ? _typeComboBox.Items[e.Index] as HealthCheckType?
            : _typeComboBox.SelectedItem as HealthCheckType?;
        ConfigurationIconListRenderer.DrawItem(
            e,
            _typeComboBox.Font,
            HealthCheckDisplay.Type(type),
            (graphics, bounds, color, _) =>
                ConfigurationItemIconRenderer.DrawHealthCheck(graphics, bounds, type, color)
        );
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

/// <summary>Provides consistent health-check labels for icon-backed lists and selectors.</summary>
internal static class HealthCheckDisplay
{
    internal static string Type(HealthCheckType? type) => type switch
    {
        HealthCheckType.Listener => "Listener",
        HealthCheckType.Vrcosc => "VRChat OSCQuery",
        _ => "Type missing"
    };

    internal static string ListItem(HealthCheckConfig healthCheck) =>
        (string.IsNullOrWhiteSpace(healthCheck.Name) ? "New health check" : healthCheck.Name) +
        (healthCheck.Enabled ? "" : " (disabled)");
}
