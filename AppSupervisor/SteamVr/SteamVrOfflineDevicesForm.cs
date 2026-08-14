namespace AppSupervisor.SteamVr;

/// <summary>Shows confirmed SteamVR incidents without blocking supervision or VR notification delivery.</summary>
internal sealed class SteamVrOfflineDevicesForm : Form
{
    private readonly CheckedListBox _devices = new()
    {
        Dock = DockStyle.Fill,
        CheckOnClick = true,
        IntegralHeight = false
    };
    private readonly Action<IReadOnlyList<string>> _silence;
    private readonly System.Windows.Forms.Timer _durationTimer = new()
    {
        Interval = 1_000
    };
    private IReadOnlyList<SteamVrOfflineDevice> _snapshot = [];

    /// <summary>Creates a modeless incident window whose elapsed durations refresh once per second.</summary>
    /// <param name="silence">Silences reminders for the selected incident serial numbers.</param>
    public SteamVrOfflineDevicesForm(Action<IReadOnlyList<string>> silence)
    {
        _silence = silence;
        Text = "SteamVR offline devices";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(520, 280);
        Size = new Size(680, 380);
        ShowInTaskbar = true;

        var explanation = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
            MaximumSize = new Size(640, 0),
            Text = "The devices below failed two SteamVR connection checks. Silencing stops reminders for the current outage; monitoring and automatic recovery detection continue."
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };
        var silenceAll = new Button { Text = "Silence all shown", AutoSize = true };
        var silenceSelected = new Button { Text = "Silence selected", AutoSize = true };
        silenceAll.Click += SilenceAllClicked;
        silenceSelected.Click += SilenceSelectedClicked;
        buttons.Controls.Add(silenceAll);
        buttons.Controls.Add(silenceSelected);

        Controls.Add(_devices);
        Controls.Add(buttons);
        Controls.Add(explanation);
        _durationTimer.Tick += DurationTimerTick;
        _durationTimer.Start();
    }

    /// <summary>Refreshes the shown incident list and closes the window after every device recovers.</summary>
    public void UpdateDevices(IReadOnlyList<SteamVrOfflineDevice> devices)
    {
        _snapshot = devices;

        if (devices.Count == 0)
        {
            if (Visible)
                Close();
            return;
        }

        RefreshDeviceList();
    }

    /// <summary>Rebuilds display text while retaining the user's checked device selection.</summary>
    private void RefreshDeviceList()
    {
        var checkedSerials = _devices.CheckedItems
            .OfType<OfflineDeviceItem>()
            .Select(item => item.SerialNumber)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _devices.BeginUpdate();
        _devices.Items.Clear();

        foreach (SteamVrOfflineDevice device in _snapshot)
        {
            string duration = FormatDuration(DateTime.UtcNow - device.OfflineSinceUtc);
            string status = device.Silenced ? "silenced" : "reminders active";
            _devices.Items.Add(
                new OfflineDeviceItem(
                    device.SerialNumber,
                    $"{SteamVrDeviceDisplay.Description(device.Name, device.DeviceClass, device.Role)}, " +
                    $"offline for {duration}, {status}"
                ),
                isChecked: checkedSerials.Contains(device.SerialNumber)
            );
        }

        _devices.EndUpdate();
    }

    /// <summary>Refreshes elapsed offline durations without waiting for another SteamVR scan.</summary>
    /// <param name="sender">The duration timer.</param>
    /// <param name="e">The timer event data.</param>
    private void DurationTimerTick(object? sender, EventArgs e)
    {
        if (_snapshot.Count > 0)
            RefreshDeviceList();
    }

    private void SilenceSelectedClicked(object? sender, EventArgs e)
    {
        string[] serials = _devices.CheckedItems
            .OfType<OfflineDeviceItem>()
            .Select(item => item.SerialNumber)
            .ToArray();

        if (serials.Length > 0)
            _silence(serials);
    }

    private void SilenceAllClicked(object? sender, EventArgs e)
        => _silence(_snapshot.Select(device => device.SerialNumber).ToArray());

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;
        if (duration.TotalMinutes < 1)
            return $"{(int)duration.TotalSeconds} sec";
        if (duration.TotalHours < 1)
            return $"{Math.Max(1, (int)duration.TotalMinutes)} min";
        return $"{(int)duration.TotalHours} h {duration.Minutes} min";
    }

    /// <summary>Stops the elapsed-time timer and releases its native resources with the window.</summary>
    /// <param name="disposing">Whether managed resources should be disposed.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _durationTimer.Stop();
            _durationTimer.Tick -= DurationTimerTick;
            _durationTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed record OfflineDeviceItem(string SerialNumber, string DisplayText)
    {
        public override string ToString() => DisplayText;
    }
}
