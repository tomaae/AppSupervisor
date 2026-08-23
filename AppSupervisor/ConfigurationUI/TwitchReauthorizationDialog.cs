namespace AppSupervisor.ConfigurationUI;

/// <summary>Prompts for fresh Twitch consent when the stored refresh chain is no longer usable.</summary>
internal sealed class TwitchReauthorizationDialog : Form
{
    public TwitchReauthorizationDialog(string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);

        Text = "Reconnect Twitch";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(560, 205);

        var reconnectButton = new Button
        {
            Text = "Reconnect Twitch",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            MinimumSize = new Size(128, 30)
        };
        var laterButton = new Button
        {
            Text = "Later",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            MinimumSize = new Size(80, 30)
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty
        };
        buttons.Controls.Add(laterButton);
        buttons.Controls.Add(reconnectButton);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 3
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Twitch authorization needs to be renewed."
        });
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Padding = new Padding(0, 12, 0, 12),
            Text = $"{detail}\n\nReconnect before the next Twitch action is needed."
        });
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        AcceptButton = reconnectButton;
        CancelButton = laterButton;
    }
}
