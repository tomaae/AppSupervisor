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

    [Fact]
    public void ProcessSnapshot_TrackerNeverConnectedThisSession_DoesNotRaiseIncident()
    {
        using var monitor = CreateMonitor(seenConnectedThisSession: false);
        var alerts = new List<SupervisorNotification>();
        monitor.AlertRequested += alerts.Add;
        SteamVrSnapshot missing = CreateSnapshot(connected: false);

        monitor.ProcessSnapshot(missing, SessionStartUtc + TimeSpan.FromSeconds(30));
        monitor.ProcessSnapshot(missing, SessionStartUtc + TimeSpan.FromSeconds(60));
        monitor.ProcessSnapshot(missing, SessionStartUtc + TimeSpan.FromMinutes(5));

        Assert.False(monitor.HasOfflineDevices);
        Assert.Empty(alerts);
    }

    [Fact]
    public void ProcessSnapshot_TrackerConnectedDuringGrace_IsMonitoredAfterDisconnect()
    {
        using var monitor = CreateMonitor(seenConnectedThisSession: false);
        SteamVrSnapshot missing = CreateSnapshot(connected: false);

        monitor.ProcessSnapshot(
            CreateSnapshot(connected: true),
            SessionStartUtc + TimeSpan.FromSeconds(10)
        );
        monitor.ProcessSnapshot(missing, SessionStartUtc + TimeSpan.FromSeconds(30));
        monitor.ProcessSnapshot(missing, SessionStartUtc + TimeSpan.FromSeconds(60));

        Assert.Single(monitor.OfflineDevices);
    }

    [Theory]
    [InlineData(SteamVrDeviceClass.Controller)]
    [InlineData(SteamVrDeviceClass.TrackingReference)]
    public void ProcessSnapshot_MandatoryDeviceNeverConnected_RaisesIncident(
        SteamVrDeviceClass deviceClass)
    {
        using var monitor = new SteamVrDeviceMonitor(new UnusedSource());
        SteamVrIntegrationConfig configuration = CreateConfiguration([]);
        configuration.Devices[0].DeviceClass = deviceClass;
        configuration.Devices[0].Role = deviceClass == SteamVrDeviceClass.Controller
            ? SteamVrDeviceRole.LeftHand
            : SteamVrDeviceRole.None;
        monitor.ApplyConfiguration(configuration);

        SteamVrSnapshot missing = CreateSnapshot(connected: false);
        monitor.ProcessSnapshot(missing, SessionStartUtc + TimeSpan.FromSeconds(30));
        monitor.ProcessSnapshot(missing, SessionStartUtc + TimeSpan.FromSeconds(60));

        Assert.Single(monitor.OfflineDevices);
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
        Assert.Contains("Waist tracker", alert.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProcessSnapshot_ObservedTrackerAssignment_UsesRoleInAlert()
    {
        using var monitor = CreateMonitor([NotificationTarget.XsOverlay]);
        SupervisorNotification? alert = null;
        monitor.AlertRequested += notification => alert = notification;
        var disconnected = new SteamVrSnapshot(
            true,
            SessionStartUtc,
            [
                new SteamVrDeviceSnapshot(
                    "LHR-TEST",
                    "Test tracker",
                    SteamVrDeviceClass.GenericTracker,
                    false,
                    SteamVrDeviceRole.LeftKnee
                )
            ]
        );

        monitor.ProcessSnapshot(disconnected, SessionStartUtc + TimeSpan.FromSeconds(30));
        monitor.ProcessSnapshot(disconnected, SessionStartUtc + TimeSpan.FromSeconds(60));

        Assert.NotNull(alert);
        Assert.Contains("Left knee tracker", alert.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SteamVrDeviceRole.LeftKnee, Assert.Single(monitor.OfflineDevices).Role);
    }

    [Theory]
    [InlineData(false, SteamVrDeviceRole.None)]
    [InlineData(true, SteamVrDeviceRole.None)]
    [InlineData(false, (SteamVrDeviceRole)999)]
    [InlineData(true, (SteamVrDeviceRole)999)]
    public void ProcessSnapshot_UnavailableRole_PreservesAssignmentInAlertsAndRecovery(
        bool observeNewAssignment,
        SteamVrDeviceRole unavailableRole)
    {
        using var monitor = CreateMonitor([NotificationTarget.XsOverlay], seenConnectedThisSession: false);
        var notifications = new List<SupervisorNotification>();
        monitor.AlertRequested += notifications.Add;
        monitor.NotificationRequested += notifications.Add;
        SteamVrDeviceRole expectedRole = observeNewAssignment
            ? SteamVrDeviceRole.LeftKnee
            : SteamVrDeviceRole.Waist;

        if (observeNewAssignment)
        {
            monitor.ProcessSnapshot(
                CreateSnapshot(connected: true, role: expectedRole),
                SessionStartUtc + TimeSpan.FromSeconds(1)
            );
        }

        var connectedUnassigned = new SteamVrSnapshot(
            true,
            SessionStartUtc,
            [
                new SteamVrDeviceSnapshot(
                    "LHR-TEST",
                    "Test tracker",
                    SteamVrDeviceClass.GenericTracker,
                    true,
                    unavailableRole
                )
            ]
        );
        SteamVrSnapshot missing = CreateSnapshot(connected: false);

        monitor.ProcessSnapshot(
            connectedUnassigned,
            SessionStartUtc + TimeSpan.FromSeconds(10)
        );
        monitor.ProcessSnapshot(missing, SessionStartUtc + TimeSpan.FromSeconds(30));
        monitor.ProcessSnapshot(missing, SessionStartUtc + TimeSpan.FromSeconds(60));
        monitor.ProcessSnapshot(missing, SessionStartUtc + TimeSpan.FromMinutes(6));

        Assert.Equal(expectedRole, Assert.Single(monitor.OfflineDevices).Role);
        monitor.ProcessSnapshot(connectedUnassigned, SessionStartUtc + TimeSpan.FromMinutes(7));

        Assert.False(monitor.HasOfflineDevices);
        Assert.Equal(3, notifications.Count);
        Assert.All(notifications, notification => Assert.Contains(
            $"— {SteamVrDeviceDisplay.RoleName(expectedRole)} tracker",
            notification.Message,
            StringComparison.Ordinal
        ));
    }

    [Fact]
    public void ProcessSnapshot_NoSavedOrObservedAssignment_ReportsUnassigned()
    {
        using var monitor = new SteamVrDeviceMonitor(new UnusedSource());
        SteamVrIntegrationConfig configuration = CreateConfiguration([]);
        configuration.Devices[0].Role = SteamVrDeviceRole.None;
        monitor.ApplyConfiguration(configuration);
        SupervisorNotification? alert = null;
        monitor.AlertRequested += notification => alert = notification;

        monitor.ProcessSnapshot(
            CreateSnapshot(connected: true, role: SteamVrDeviceRole.None),
            SessionStartUtc + TimeSpan.FromSeconds(1)
        );
        monitor.ProcessSnapshot(CreateSnapshot(connected: false), SessionStartUtc + TimeSpan.FromSeconds(30));
        monitor.ProcessSnapshot(CreateSnapshot(connected: false), SessionStartUtc + TimeSpan.FromSeconds(60));

        Assert.NotNull(alert);
        Assert.Contains("Unassigned tracker", alert.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SteamVrDeviceRole.None, Assert.Single(monitor.OfflineDevices).Role);
    }

    [Theory]
    [InlineData(SteamVrDeviceRole.None)]
    [InlineData(SteamVrDeviceRole.Waist)]
    public void ApplyConfiguration_UnchangedSavedRole_PreservesObservedAssignment(
        SteamVrDeviceRole savedRole)
    {
        using var monitor = new SteamVrDeviceMonitor(new UnusedSource());
        SteamVrIntegrationConfig configuration = CreateConfiguration([]);
        configuration.Devices[0].Role = savedRole;
        monitor.ApplyConfiguration(configuration);
        monitor.ProcessSnapshot(
            CreateSnapshot(connected: true, role: SteamVrDeviceRole.LeftKnee),
            SessionStartUtc + TimeSpan.FromSeconds(1)
        );
        configuration.ReminderIntervalMinutes = 10;
        monitor.ApplyConfiguration(configuration);
        monitor.ProcessSnapshot(CreateSnapshot(connected: false), SessionStartUtc + TimeSpan.FromSeconds(30));
        monitor.ProcessSnapshot(CreateSnapshot(connected: false), SessionStartUtc + TimeSpan.FromSeconds(60));

        Assert.Equal(SteamVrDeviceRole.LeftKnee, Assert.Single(monitor.OfflineDevices).Role);
    }

    [Theory]
    [InlineData(SteamVrDeviceRole.None)]
    [InlineData(SteamVrDeviceRole.LeftFoot)]
    public void ApplyConfiguration_ChangedSavedRole_UpdatesIncidentAssignment(
        SteamVrDeviceRole replacementRole)
    {
        using var monitor = CreateMonitor();
        monitor.ProcessSnapshot(CreateSnapshot(connected: false), SessionStartUtc + TimeSpan.FromSeconds(30));
        monitor.ProcessSnapshot(CreateSnapshot(connected: false), SessionStartUtc + TimeSpan.FromSeconds(60));
        SteamVrOfflineDevice original = Assert.Single(monitor.OfflineDevices);
        monitor.Silence(["LHR-TEST"]);
        SteamVrIntegrationConfig configuration = CreateConfiguration([]);
        configuration.Devices[0].Role = replacementRole;

        monitor.ApplyConfiguration(configuration);

        SteamVrOfflineDevice updated = Assert.Single(monitor.OfflineDevices);
        Assert.Equal(replacementRole, updated.Role);
        Assert.Equal(original.OfflineSinceUtc, updated.OfflineSinceUtc);
        Assert.True(updated.Silenced);
    }

    [Fact]
    public void Silence_DeviceForSession_SuppressesReminderAndLaterDisconnectionAlert()
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

        monitor.ProcessSnapshot(missing, failedAt + TimeSpan.FromMinutes(6));
        monitor.ProcessSnapshot(missing, failedAt + TimeSpan.FromMinutes(6.5));

        Assert.True(Assert.Single(monitor.OfflineDevices).Silenced);
        Assert.Single(notifications, notification => notification.Severity == NotificationSeverity.Error);
    }

    [Fact]
    public void ProcessSnapshot_NewSteamVrSession_ClearsDeviceSilence()
    {
        using var monitor = CreateMonitor();
        var alerts = new List<SupervisorNotification>();
        monitor.AlertRequested += alerts.Add;
        SteamVrSnapshot missing = CreateSnapshot(connected: false);

        monitor.ProcessSnapshot(missing, SessionStartUtc + TimeSpan.FromSeconds(30));
        monitor.ProcessSnapshot(missing, SessionStartUtc + TimeSpan.FromSeconds(60));
        monitor.Silence(["LHR-TEST"]);

        DateTime nextSessionStart = SessionStartUtc + TimeSpan.FromHours(1);
        monitor.ProcessSnapshot(
            CreateSnapshot(connected: true, nextSessionStart),
            nextSessionStart + TimeSpan.FromSeconds(1)
        );
        SteamVrSnapshot missingInNextSession = CreateSnapshot(
            connected: false,
            nextSessionStart
        );
        monitor.ProcessSnapshot(
            missingInNextSession,
            nextSessionStart + TimeSpan.FromSeconds(30)
        );
        monitor.ProcessSnapshot(
            missingInNextSession,
            nextSessionStart + TimeSpan.FromSeconds(60)
        );

        Assert.False(Assert.Single(monitor.OfflineDevices).Silenced);
        Assert.Equal(2, alerts.Count);
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
        IReadOnlyList<NotificationTarget>? notificationTargets = null,
        bool seenConnectedThisSession = true)
    {
        var monitor = new SteamVrDeviceMonitor(new UnusedSource());
        monitor.ApplyConfiguration(CreateConfiguration(notificationTargets));

        if (seenConnectedThisSession)
        {
            monitor.ProcessSnapshot(
                CreateSnapshot(connected: true),
                SessionStartUtc + TimeSpan.FromSeconds(1)
            );
        }

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
                    ModelNumber = "Test tracker",
                    Role = SteamVrDeviceRole.Waist
                }
            ],
            Notifications = new NotificationConfig
            {
                Target = notificationTargets?.ToList() ?? []
            }
        };
    }

    private static SteamVrSnapshot CreateSnapshot(
        bool connected,
        DateTime? sessionStartUtc = null,
        SteamVrDeviceRole role = SteamVrDeviceRole.Waist)
    {
        IReadOnlyList<SteamVrDeviceSnapshot> devices = connected
            ?
            [
                new SteamVrDeviceSnapshot(
                    "LHR-TEST",
                    "Test tracker",
                    SteamVrDeviceClass.GenericTracker,
                    true,
                    role
                )
            ]
            : [];
        return new SteamVrSnapshot(true, sessionStartUtc ?? SessionStartUtc, devices);
    }

    private sealed class UnusedSource : ISteamVrDeviceSource
    {
        public SteamVrSnapshot Capture() => new(false, null, []);
        public void Dispose()
        {
        }
    }
}
