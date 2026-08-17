namespace AppSupervisor.ConfigurationUI;

/// <summary>Shows one centered loading message and activity indicator over a picker list.</summary>
internal sealed class PickerLoadingOverlay : Panel
{
    private readonly Control _host;
    private readonly Font _boldFont;

    /// <summary>Attaches a centered loading message above the list hosted by one fill panel.</summary>
    /// <param name="host">The panel containing the picker result list.</param>
    public PickerLoadingOverlay(Control host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _boldFont = new Font(host.Font, host.Font.Style | FontStyle.Bold);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = SystemColors.Window;
        BorderStyle = BorderStyle.FixedSingle;
        Padding = Padding.Empty;

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = new Padding(12, 10, 12, 10)
        };
        layout.Controls.Add(new Label
        {
            Anchor = AnchorStyles.None,
            AutoSize = true,
            Font = _boldFont,
            ForeColor = SystemColors.ControlText,
            Margin = new Padding(0, 0, 0, 8),
            Text = "Loading...",
            UseMnemonic = false
        }, 0, 0);
        layout.Controls.Add(new ProgressBar
        {
            Anchor = AnchorStyles.None,
            MarqueeAnimationSpeed = 30,
            Margin = Padding.Empty,
            Size = new Size(160, 8),
            Style = ProgressBarStyle.Marquee
        }, 0, 1);
        Controls.Add(layout);
        host.Controls.Add(this);
        host.Resize += HostResized;
        SizeChanged += OverlaySizeChanged;
        ShowLoading();
    }

    /// <summary>Makes the loading message visible above the result list.</summary>
    public void ShowLoading()
    {
        Visible = true;
        BringToFront();
        CenterInHost();
    }

    /// <summary>Removes the loading message after discovery stops.</summary>
    public void HideLoading() => Visible = false;

    /// <summary>Unsubscribes layout events and releases the dedicated bold font.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _host.Resize -= HostResized;
            SizeChanged -= OverlaySizeChanged;
        }

        base.Dispose(disposing);

        if (disposing)
            _boldFont.Dispose();
    }

    private void HostResized(object? sender, EventArgs e) => CenterInHost();

    private void OverlaySizeChanged(object? sender, EventArgs e) => CenterInHost();

    private void CenterInHost()
    {
        Location = new Point(
            Math.Max(0, (_host.ClientSize.Width - Width) / 2),
            Math.Max(0, (_host.ClientSize.Height - Height) / 2)
        );
    }
}
