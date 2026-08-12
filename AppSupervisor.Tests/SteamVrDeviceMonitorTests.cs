using AppSupervisor.Notifications;
using AppSupervisor.SteamVr;

namespace AppSupervisor.Tests;

/// <summary>Verifies SteamVR grace, confirmation, reminders, acknowledgement, and recovery semantics.</summary>
public sealed class SteamVrDeviceMonitorTests
{
    private static readonly DateTime SessionStartUtc = new(2026, 8, 12, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ProcessSnapshot_MissingDevice_RequiresGraceAndTwoThirtySecondChecks()
    {
        using var monitor = CreateMonitor();
        SteamVrSnapshot missing = CreateSnapshot(connected: false);

        monitor.ProcessSnapshot(missing, SessionStartUtc + TimeSpan.FromSeconds(29));
        monitor.ProcessSnapshot(missing, SessionStartUtc + TimeSpan.FromSeconds(30));

        Assert.False(monitor.HasOfflineDevices);

        monitor.ProcessSnapshot(missing, SessionStartUtc + TimeSpan.FromSeconds(60));

        SteamVrOfflineDevice incident = Assert.Single(monitor.OfflineDevices);
        Assert.Equal("Waist tracker", incident.Name);
        Assert.False(incident.Silenced);
    }

    /// <summary>
    /// Confirms a newly offline device raises one inseparable alert containing configured XSOverlay content.
    /// </summary>
    [Fact]
    public void ProcessSnapshot_ConfirmedIncident_RaisesTargetedAlert()
    {
        using var monitor = CreateMonitor([NotificationTarget.XsOverlay]);
        SupervisorNotification? alert = null;
        monitor.AlertRequested += notification => alert = notification;
        SteamVrSnapshot missing = CreateSnapshot(connected: false);

        monitor.ProcessSnapshot(missing, SessionStartUtc + TimeSpan.FromSeconds(30));
        monitor.ProcessSnapshot(missing, SessionStartUtc + TimeSpan.FromSeconds(60));

        Assert.NotNull(alert);
        Assert.Equal([NotificationTarget.XsOverlay], alert.Targets);
        Assert.Equal(NotificationSeverity.Error, alert.Severity);
    }

    [Fact]
    public void Silence_CurrentIncident_SuppressesReminderButRecoveryStillClearsIt()
    {
        using var monitor = CreateMonitor();
        var notifications = new List<SupervisorNotification>();
        monitor.NotificationRequested += notifications.Add;
        monitor.AlertRequested += notifications.Add;
        SteamVrSnapshot missing = CreateSnapshot(connected: false);
        DateTime firstCheck = SessionStartUtc + TimeSpan.FromSeconds(30);
        DateTime failedAt = firstCheck + TimeSpan.FromSeconds(30);

        monitor.ProcessSnapshot(missing, firstCheck);
        monitor.ProcessSnapshot(missing, failedAt);
        Assert.Single(notifications, notification => notification.Severity == NotificationSeverity.Error);

        monitor.Silence(["LHR-TEST"]);
        Assert.True(Assert.Single(monitor.OfflineDevices).Silenced);

        monitor.ProcessSnapshot(missing, failedAt + TimeSpan.FromMinutes(5));
        Assert.Single(notifications, notification => notification.Severity == NotificationSeverity.Error);

        monitor.ProcessSnapshot(CreateSnapshot(connected: true), failedAt + TimeSpan.FromMinutes(5.5));

        Assert.False(monitor.HasOfflineDevices);
        Assert.Contains(notifications, notification =>
            notification.Severity == NotificationSeverity.Information &&
            notification.Title.Contains("recovered", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProcessSnapshot_UnsilencedIncident_PublishesFiveMinuteReminder()
    {
        using var monitor = CreateMonitor();
        var notifications = new List<SupervisorNotification>();
        monitor.NotificationRequested += notifications.Add;
        monitor.AlertRequested += notifications.Add;
        SteamVrSnapshot missing = CreateSnapshot(connected: false);
        DateTime failedAt = SessionStartUtc + TimeSpan.FromSeconds(60);

        monitor.ProcessSnapshot(missing, SessionStartUtc + TimeSpan.FromSeconds(30));
        monitor.ProcessSnapshot(missing, failedAt);
        monitor.ProcessSnapshot(missing, failedAt + TimeSpan.FromMinutes(5));

        SupervisorNotification[] errors = notifications
            .Where(notification => notification.Severity == NotificationSeverity.Error)
            .ToArray();
        Assert.Equal(2, errors.Length);
        Assert.Contains("still offline", errors[1].Title, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Confirms the one-second scheduler tick publishes a due reminder without waiting for another device scan.
    /// </summary>
    [Fact]
    public void Advance_ReminderDeadlineElapsed_PublishesWithoutCompletedCapture()
    {
        using var monitor = CreateMonitor([NotificationTarget.XsOverlay]);
        SteamVrIntegrationConfig configuration = CreateConfiguration([NotificationTarget.XsOverlay]);
        configuration.ReminderIntervalMinutes = 1;
        monitor.ApplyConfiguration(configuration);
        var alerts = new List<SupervisorNotification>();
        monitor.AlertRequested += alerts.Add;
        SteamVrSnapshot missing = CreateSnapshot(connected: false);
        DateTime failedAt = SessionStartUtc + TimeSpan.FromSeconds(60);
        monitor.ProcessSnapshot(missing, SessionStartUtc + TimeSpan.FromSeconds(30));
        monitor.ProcessSnapshot(missing, failedAt);
        alerts.Clear();

        monitor.Advance(failedAt + TimeSpan.FromMinutes(1));

        SupervisorNotification reminder = Assert.Single(alerts);
        Assert.Contains("still offline", reminder.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([NotificationTarget.XsOverlay], reminder.Targets);
    }

    [Fact]
    public void ProcessSnapshot_SteamVrStops_ClearsIncidentWithoutRecoveryAlert()
    {
        using var monitor = CreateMonitor();
        var notifications = new List<SupervisorNotification>();
        IReadOnlyList<SteamVrOfflineDevice>? latestOfflineDevices = null;
        monitor.NotificationRequested += notifications.Add;
        monitor.AlertRequested += notifications.Add;
        monitor.OfflineDevicesChanged += devices => latestOfflineDevices = devices;
        SteamVrSnapshot missing = CreateSnapshot(connected: false);

        monitor.ProcessSnapshot(missing, SessionStartUtc + TimeSpan.FromSeconds(30));
        monitor.ProcessSnapshot(missing, SessionStartUtc + TimeSpan.FromSeconds(60));
        notifications.Clear();

        monitor.ProcessSnapshot(new SteamVrSnapshot(false, null, []), SessionStartUtc + TimeSpan.FromSeconds(90));

        Assert.False(monitor.HasOfflineDevices);
        Assert.Empty(notifications);
        Assert.NotNull(latestOfflineDevices);
        Assert.Empty(latestOfflineDevices);
    }

    /// <summary>
    /// Confirms saving enabled SteamVR settings preserves an active incident and applies new notification targets.
    /// </summary>
    [Fact]
    public void ApplyConfiguration_EnabledReload_PreservesIncidentAndUpdatesTargets()
    {
        using var monitor = CreateMonitor([NotificationTarget.XsOverlay]);
        SteamVrSnapshot missing = CreateSnapshot(connected: false);
        DateTime failedAt = SessionStartUtc + TimeSpan.FromSeconds(60);
        monitor.ProcessSnapshot(missing, SessionStartUtc + TimeSpan.FromSeconds(30));
        monitor.ProcessSnapshot(missing, failedAt);
        SteamVrOfflineDevice original = Assert.Single(monitor.OfflineDevices);
        SupervisorNotification? reminder = null;
        monitor.AlertRequested += notification => reminder = notification;
        SteamVrIntegrationConfig replacement = CreateConfiguration([NotificationTarget.Windows]);
        replacement.Devices[0].Name = "Renamed waist tracker";

        monitor.ApplyConfiguration(replacement);
        SteamVrOfflineDevice preserved = Assert.Single(monitor.OfflineDevices);
        monitor.ProcessSnapshot(missing, failedAt + TimeSpan.FromMinutes(5));

        Assert.Equal(original.OfflineSinceUtc, preserved.OfflineSinceUtc);
        Assert.Equal("Renamed waist tracker", preserved.Name);
        Assert.NotNull(reminder);
        Assert.Equal([NotificationTarget.Windows], reminder.Targets);
    }

    /// <summary>Creates a configured monitor for one expected tracker.</summary>
    /// <param name="notificationTargets">The destinations assigned to SteamVR incidents.</param>
    /// <returns>A freshly configured monitor.</returns>
    private static SteamVrDeviceMonitor CreateMonitor(
        IReadOnlyList<NotificationTarget>? notificationTargets = null)
    {
        var monitor = new SteamVrDeviceMonitor(new UnusedSource());
        monitor.ApplyConfiguration(CreateConfiguration(notificationTargets));
        return monitor;
    }

    /// <summary>Creates enabled SteamVR settings for one expected tracker.</summary>
    /// <param name="notificationTargets">The destinations assigned to SteamVR incidents.</param>
    /// <returns>The detached integration configuration.</returns>
    private static SteamVrIntegrationConfig CreateConfiguration(
        IReadOnlyList<NotificationTarget>? notificationTargets)
    {
        return new SteamVrIntegrationConfig
        {
            Enabled = true,
            ReminderIntervalMinutes = 5,
            Devices =
            [
                new SteamVrDeviceConfig
                {
                    Enabled = true,
                    SerialNumber = "LHR-TEST",
                    Name = "Waist tracker",
                    DeviceClass = SteamVrDeviceClass.GenericTracker,
                    ModelNumber = "Test tracker"
                }
            ],
            Notifications = new NotificationConfig
            {
                Target = notificationTargets?.ToList() ?? []
            }
        };
    }

    private static SteamVrSnapshot CreateSnapshot(bool connected)
    {
        IReadOnlyList<SteamVrDeviceSnapshot> devices = connected
            ? [new SteamVrDeviceSnapshot("LHR-TEST", "Test tracker", SteamVrDeviceClass.GenericTracker, true)]
            : [];
        return new SteamVrSnapshot(true, SessionStartUtc, devices);
    }

    private sealed class UnusedSource : ISteamVrDeviceSource
    {
        public SteamVrSnapshot Capture() => new(false, null, []);
        public void Dispose()
        {
        }
    }
}
