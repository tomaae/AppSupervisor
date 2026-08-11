using AppSupervisor.Configuration;
using AppSupervisor.Health;
using AppSupervisor.Notifications;
using AppSupervisor.ServiceControl;

namespace AppSupervisor.ConfigurationUI;

/// <summary>
/// Adds process selection, Steam launch editing, and one-shot notification and health-check diagnostics to the editor.
/// </summary>
public sealed partial class ConfigurationEditorForm
{
    private readonly Action<SupervisorNotification>? _notificationPublisher;

    /// <summary>Creates an editor connected to the running supervisor's production notification router.</summary>
    /// <param name="configPath">The active config.json path.</param>
    /// <param name="notificationPublisher">The production notification publishing callback.</param>
    public ConfigurationEditorForm(
        string configPath,
        Action<SupervisorNotification> notificationPublisher)
        : this(
            configPath,
            cancellationToken => Task.Run(
                InstalledServiceCatalog.LoadThirdPartyServices,
                cancellationToken
            ),
            notificationPublisher
        )
    {
    }

    /// <summary>Builds a notification target selector with a production-path test button.</summary>
    /// <param name="selector">The target selector to display.</param>
    /// <param name="testHandler">The matching test-notification event handler.</param>
    /// <returns>A horizontal target and test-button panel.</returns>
    private Control BuildNotificationTestPanel(
        NotificationTargetsControl selector,
        EventHandler testHandler)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty
        };
        selector.Margin = Padding.Empty;
        panel.Controls.Add(selector);
        panel.Controls.Add(CreateButton("Test notification", testHandler));
        return panel;
    }

    /// <summary>Lets the user select a monitored executable and stores only its filename as the process trigger.</summary>
    /// <param name="sender">The monitored-process Browse button.</param>
    /// <param name="e">The click event data.</param>
    private void BrowseMonitorProcessClicked(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose monitored executable",
            Filter = "Applications (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
            _monitorProcess.Text = Path.GetFileName(dialog.FileName);
    }

    /// <summary>Lets the user use the full executable path of a currently running helper process.</summary>
    /// <param name="sender">The helper Pick running button.</param>
    /// <param name="e">The click event data.</param>
    private void PickApplicationProcessClicked(object? sender, EventArgs e)
    {
        using var picker = new RunningProcessPickerDialog();

        if (picker.ShowDialog(this) != DialogResult.OK)
            return;

        if (string.IsNullOrWhiteSpace(picker.SelectedExecutablePath))
        {
            MessageBox.Show(
                this,
                "Windows did not allow AppSupervisor to read that process's executable path.",
                "Executable path unavailable",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
            return;
        }

        _applicationPath.Text = picker.SelectedExecutablePath;
    }

    /// <summary>Sends a test through the selected helper application's production notification targets.</summary>
    /// <param name="sender">The application Test notification button.</param>
    /// <param name="e">The click event data.</param>
    private void TestApplicationNotificationClicked(object? sender, EventArgs e)
    {
        PublishTestNotification(
            _applicationNotifications.SelectedTargets,
            SelectedApplication is null
                ? "Helper application"
                : SafeFileName(SelectedApplication.Path, "Helper application")
        );
    }

    /// <summary>Sends a test through the selected service's production notification targets.</summary>
    /// <param name="sender">The service Test notification button.</param>
    /// <param name="e">The click event data.</param>
    private void TestServiceNotificationClicked(object? sender, EventArgs e)
    {
        PublishTestNotification(
            _serviceNotifications.SelectedTargets,
            SelectedService is null
                ? "Windows service"
                : DisplayName(SelectedService.ServiceName, "Windows service")
        );
    }

    /// <summary>Sends a test through the selected health check's configured production targets.</summary>
    /// <param name="sender">The health-check Test notification button.</param>
    /// <param name="e">The click event data.</param>
    private void TestHealthNotificationClicked(object? sender, EventArgs e)
    {
        if (SelectedHealthCheck is not HealthCheckConfig healthCheck)
            return;

        PublishTestNotification(
            healthCheck.Notifications.Target,
            DisplayName(healthCheck.Name, "Health check")
        );
    }

    /// <summary>Publishes one informational test through the running supervisor's normal provider and failover routing.</summary>
    /// <param name="targets">The configured destinations to exercise.</param>
    /// <param name="sourceName">The resource name displayed in test content.</param>
    private void PublishTestNotification(
        IReadOnlyList<NotificationTarget> targets,
        string sourceName)
    {
        if (targets.Count == 0)
        {
            MessageBox.Show(
                this,
                "Select at least one notification target first.",
                "No notification target",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
            return;
        }

        if (_notificationPublisher is null)
        {
            MessageBox.Show(
                this,
                "Notification testing is available when this editor is opened from the running AppSupervisor tray icon.",
                "Notification test unavailable",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
            return;
        }

        _notificationPublisher(new SupervisorNotification(
            NotificationSeverity.Information,
            "AppSupervisor test notification",
            $"Notification delivery for {sourceName} is working.",
            targets
        ));
    }

    /// <summary>Runs the selected health probe once without changing counters, recovery state, or external processes.</summary>
    /// <param name="sender">The Test check button.</param>
    /// <param name="e">The click event data.</param>
    private async void TestHealthCheckClicked(object? sender, EventArgs e)
    {
        if (SelectedApplication is not ManagedApplicationConfig application ||
            SelectedHealthCheck is not HealthCheckConfig selected)
        {
            return;
        }

        Button? testButton = sender as Button;
        testButton?.Enabled = false;

        try
        {
            HealthCheckConfig healthCheck = ConfigJson.Clone(selected);
            ValidateHealthCheckForTest(application.Path, healthCheck);
            IHealthCheckActivationCondition activationCondition =
                HealthCheckFactory.CreateActivationCondition(healthCheck);

            if (!activationCondition.IsActive())
            {
                MessageBox.Show(
                    this,
                    "The health check's prerequisite process is not currently running.",
                    "Health check inactive",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
                return;
            }

            IReadOnlySet<int> ownerProcessIds =
                ProcessPathDiscovery.FindRunningProcessIds(application.Path);
            using IHealthProbe probe = HealthCheckFactory.CreateProbe(healthCheck);
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(healthCheck.TimeoutSeconds)
            );
            HealthProbeResult result = await probe.CheckAsync(
                ownerProcessIds,
                timeout.Token
            );

            MessageBox.Show(
                this,
                result.Detail,
                result.Healthy ? "Health check succeeded" : "Health check failed",
                MessageBoxButtons.OK,
                result.Healthy ? MessageBoxIcon.Information : MessageBoxIcon.Warning
            );
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show(
                this,
                "The health check timed out.",
                "Health check failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Health check could not run",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }
        finally
        {
            if (testButton is not null && !testButton.IsDisposed)
                testButton.Enabled = true;
        }
    }

    /// <summary>Validates one detached check using its actual helper executable identity.</summary>
    /// <param name="applicationPath">The selected helper executable path.</param>
    /// <param name="healthCheck">The detached health check to validate.</param>
    private static void ValidateHealthCheckForTest(
        string applicationPath,
        HealthCheckConfig healthCheck)
    {
        var profile = new SupervisorProfileConfig
        {
            Name = "Health check test",
            MonitorProcess = "notepad.exe",
            Applications =
            [
                new ManagedApplicationConfig
                {
                    Path = applicationPath,
                    Notifications = new NotificationConfig { Target = [] },
                    HealthChecks = [healthCheck]
                }
            ],
            Services = []
        };
        ConfigValidator.Validate([profile]);
    }
}
