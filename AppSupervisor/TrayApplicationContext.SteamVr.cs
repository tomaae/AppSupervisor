using AppSupervisor.Notifications;
using AppSupervisor.SteamVr;

namespace AppSupervisor;

/// <summary>Integrates global SteamVR incidents with notifications, tray state, and a modeless alert window.</summary>
public partial class TrayApplicationContext
{
    private readonly SteamVrDeviceMonitor _steamVrMonitor = new(new OpenVrDeviceSource());
    private readonly ToolStripMenuItem _steamVrAlertsItem = new("SteamVR offline devices...");
    private SteamVrOfflineDevicesForm? _steamVrAlertForm;
    private IReadOnlyList<SteamVrOfflineDevice> _steamVrOfflineDevices = [];
    private volatile bool _hasSteamVrOfflineDevices;
    private volatile bool _steamVrMonitoringEnabled;

    private void InitializeSteamVrIntegration()
    {
        _steamVrMonitor.NotificationRequested += OnSteamVrNotificationRequested;
        _steamVrMonitor.OfflineDevicesChanged += OnSteamVrOfflineDevicesChanged;
        _steamVrMonitor.AlertRequested += OnSteamVrAlertRequested;
        _steamVrAlertsItem.Click += OpenSteamVrAlertsClicked;
        UpdateSteamVrAlertMenu();
    }

    private void OnSteamVrNotificationRequested(SupervisorNotification notification)
        => _notificationService.Publish(notification);

    /// <summary>Publishes one offline-device notification before showing its corresponding incident window.</summary>
    /// <param name="notification">The targeted initial failure or reminder notification.</param>
    private void OnSteamVrAlertRequested(SupervisorNotification notification)
    {
        _notificationService.Publish(notification);
        SteamVrOfflineDevice[] devices = _steamVrMonitor.OfflineDevices.ToArray();
        _hasSteamVrOfflineDevices = devices.Length > 0;
        RunOnUiThread(() =>
        {
            ApplySteamVrOfflineDevices(devices);
            ShowSteamVrAlertWindow();
        });
    }

    /// <summary>Marshals an immutable offline-device snapshot to the WinForms thread.</summary>
    private void OnSteamVrOfflineDevicesChanged(IReadOnlyList<SteamVrOfflineDevice> devices)
    {
        SteamVrOfflineDevice[] snapshot = devices.ToArray();
        _hasSteamVrOfflineDevices = snapshot.Length > 0;
        RunOnUiThread(() => ApplySteamVrOfflineDevices(snapshot));
    }

    /// <summary>Updates the cached UI model and any open incident window.</summary>
    private void ApplySteamVrOfflineDevices(IReadOnlyList<SteamVrOfflineDevice> devices)
    {
        _steamVrOfflineDevices = devices;
        _hasSteamVrOfflineDevices = devices.Count > 0;

        if (_steamVrAlertForm is { IsDisposed: false } alertForm)
        {
            if (devices.Count == 0)
            {
                alertForm.Close();
            }
            else
            {
                alertForm.UpdateDevices(devices);
            }
        }

        UpdateSteamVrAlertMenu();
        UpdateTrayState();
    }

    private void ShowSteamVrAlertWindow()
    {
        IReadOnlyList<SteamVrOfflineDevice> devices = _steamVrOfflineDevices;

        if (devices.Count == 0)
            return;

        if (_steamVrAlertForm is null || _steamVrAlertForm.IsDisposed)
        {
            _steamVrAlertForm = new SteamVrOfflineDevicesForm(serialNumbers =>
                QueueSupervisionWork(
                    () => _steamVrMonitor.Silence(serialNumbers),
                    completed: static () => { }
                ));
            _steamVrAlertForm.FormClosed += SteamVrAlertFormClosed;
            _steamVrAlertForm.UpdateDevices(devices);
            _steamVrAlertForm.Show();
        }
        else
        {
            _steamVrAlertForm.UpdateDevices(devices);
            _steamVrAlertForm.Show();
            _steamVrAlertForm.BringToFront();
            _steamVrAlertForm.Activate();
        }
    }

    private void OpenSteamVrAlertsClicked(object? sender, EventArgs e)
        => ShowSteamVrAlertWindow();

    private void SteamVrAlertFormClosed(object? sender, FormClosedEventArgs e)
    {
        if (_steamVrAlertForm is not null)
            _steamVrAlertForm.FormClosed -= SteamVrAlertFormClosed;
        _steamVrAlertForm = null;
    }

    private void UpdateSteamVrAlertMenu()
    {
        int count = _steamVrOfflineDevices.Count;
        _steamVrAlertsItem.Enabled = count > 0;
        _steamVrAlertsItem.Text = count == 0
            ? "SteamVR offline devices..."
            : $"SteamVR offline devices ({count})...";
    }

    private void DisposeSteamVrIntegration()
    {
        _steamVrMonitor.NotificationRequested -= OnSteamVrNotificationRequested;
        _steamVrMonitor.OfflineDevicesChanged -= OnSteamVrOfflineDevicesChanged;
        _steamVrMonitor.AlertRequested -= OnSteamVrAlertRequested;
        _steamVrAlertsItem.Click -= OpenSteamVrAlertsClicked;
        _steamVrMonitor.Dispose();
    }
}
