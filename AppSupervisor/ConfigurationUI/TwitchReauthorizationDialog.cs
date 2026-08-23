using AppSupervisor.Twitch;

namespace AppSupervisor.ConfigurationUI;

/// <summary>Prompts for and directly performs fresh Twitch broadcaster consent.</summary>
internal sealed class TwitchReauthorizationDialog : Form
{
    private readonly Func<Action<string>, CancellationToken, Task<TwitchAuthorizationStatus>>
        _connect;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Label _detailLabel;
    private readonly Button _reconnectButton;
    private readonly Button _laterButton;

    public TwitchReauthorizationDialog(string detail)
        : this(detail, TwitchConnectionFlow.ConnectAsync)
    {
    }

    internal TwitchReauthorizationDialog(
        string detail,
        Func<Action<string>, CancellationToken, Task<TwitchAuthorizationStatus>> connect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        ArgumentNullException.ThrowIfNull(connect);
        _connect = connect;

        Text = "Reconnect Twitch";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(560, 205);

        _reconnectButton = new Button
        {
            Text = "Reconnect Twitch",
            AutoSize = true,
            MinimumSize = new Size(128, 30)
        };
        _reconnectButton.Click += ReconnectClicked;
        _laterButton = new Button
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
        buttons.Controls.Add(_laterButton);
        buttons.Controls.Add(_reconnectButton);

        _detailLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Padding = new Padding(0, 12, 0, 12),
            Text = $"{detail}\n\nReconnect before the next Twitch action is needed."
        };
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
        layout.Controls.Add(_detailLabel);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        AcceptButton = _reconnectButton;
        CancelButton = _laterButton;
        FormClosed += (_, _) => _lifetimeCancellation.Cancel();
    }

    private async void ReconnectClicked(object? sender, EventArgs e)
    {
        _reconnectButton.Enabled = false;
        _laterButton.Text = "Cancel";

        try
        {
            TwitchAuthorizationStatus status = await _connect(
                UpdateAuthorizationStatus,
                _lifetimeCancellation.Token
            );
            if (IsDisposed)
                return;

            _detailLabel.Text = $"Connected as {status.Login}.";
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (IsDisposed)
                return;

            _detailLabel.Text = exception.Message;
            _reconnectButton.Enabled = true;
            _laterButton.Text = "Later";
        }
    }

    private void UpdateAuthorizationStatus(string status)
    {
        if (!IsDisposed)
            _detailLabel.Text = status;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _lifetimeCancellation.Dispose();
        base.Dispose(disposing);
    }
}
