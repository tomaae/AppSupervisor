using AppSupervisor.Configuration;
using AppSupervisor.Health;
using AppSupervisor.Notifications;
using AppSupervisor.ServiceControl;
using AppSupervisor.SteamVr;

namespace AppSupervisor.ConfigurationUI;

/// <summary>
/// Adds process selection, Steam launch editing, and one-shot notification and health-check diagnostics to the editor.
/// </summary>
public sealed partial class ConfigurationEditorForm
{
    private readonly Action<SupervisorNotification>? _notificationPublisher;
    private readonly IHelperTestController? _helperTestController;
    private readonly System.Windows.Forms.Timer _helperTestAvailabilityTimer = new()
    {
        Interval = 500
    };
    private Button _testHelperButton = null!;
    private bool _helperTestCanStart;
    private bool _helperTestAvailabilityRefreshPending;
    private bool _helperTestClosePending;
    private bool _helperTestCloseReady;
    private bool _helperTestingDisposed;

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

    /// <summary>Creates an editor that shares the running monitor's serialized SteamVR source.</summary>
    internal ConfigurationEditorForm(
        string configPath,
        Action<SupervisorNotification> notificationPublisher,
        Func<CancellationToken, Task<SteamVrSnapshot>> steamVrDeviceLoader,
        IHelperTestController? helperTestController = null)
        : this(
            configPath,
            cancellationToken => Task.Run(
                InstalledServiceCatalog.LoadThirdPartyServices,
                cancellationToken
            ),
            notificationPublisher,
            steamVrDeviceLoader,
            helperTestController: helperTestController
        )
    {
    }

    /// <summary>Builds the production-lifecycle helper test action and its availability explanation.</summary>
    /// <returns>A compact button and status panel.</returns>
    private Control BuildHelperTestPanel()
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty
        };
        _testHelperButton = CreateButton("Test helper", TestHelperClicked);
        _testHelperButton.Enabled = false;
        panel.Controls.Add(_testHelperButton);
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(8, 7, 0, 0),
            Text = "Uses the normal launch, startup macros, and close behavior."
        });
        return panel;
    }

    /// <summary>Connects test-state notifications and active-profile availability polling.</summary>
    private void InitializeHelperTesting()
    {
        _helperTestAvailabilityTimer.Tick += HelperTestAvailabilityTimerTick;

        if (_helperTestController is null)
        {
            UpdateHelperTestButton();
            return;
        }

        _helperTestController.StateChanged += HelperTestControllerStateChanged;
        _helperTestAvailabilityTimer.Start();
        BeginRefreshHelperTestAvailability();
    }

    /// <summary>Starts or immediately stops the selected helper test.</summary>
    private async void TestHelperClicked(object? sender, EventArgs e)
    {
        if (_helperTestController is null)
            return;

        try
        {
            if (_helperTestController.State == HelperTestState.Idle)
            {
                if (SelectedProfile is not SupervisorProfileConfig profile ||
                    SelectedApplication is not ManagedApplicationConfig application)
                {
                    return;
                }

                ManagedApplicationConfig testConfiguration = ConfigJson.Clone(application);
                ValidateHelperForTest(testConfiguration);
                await _helperTestController.StartAsync(profile.ProfileId, testConfiguration);
            }
            else
            {
                await _helperTestController.StopAsync();
            }
        }
        catch (OperationCanceledException) when (_helperTestClosePending || IsDisposed)
        {
        }
        catch (Exception exception)
        {
            if (!IsDisposed && !_helperTestClosePending)
            {
                MessageBox.Show(
                    this,
                    exception.Message,
                    "Helper test could not complete",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                UpdateHelperTestButton();
                BeginRefreshHelperTestAvailability();
            }
        }
    }

    /// <summary>Validates the selected helper without requiring the rest of its edited profile.</summary>
    private static void ValidateHelperForTest(ManagedApplicationConfig application)
    {
        application.Enabled = true;
        application.StartupOrder = 0;
        application.DependencyResourceId = "";
        var profile = new SupervisorProfileConfig
        {
            Name = "Helper test",
            MonitorProcess = "AppSupervisor.HelperTest.Trigger.exe",
            RestartTimeoutSeconds = 20,
            Applications = [application]
        };
        ConfigValidator.Validate([profile]);
    }

    /// <summary>Coalesces the periodic live-profile availability check.</summary>
    private async void BeginRefreshHelperTestAvailability()
    {
        if (_helperTestingDisposed || _helperTestAvailabilityRefreshPending ||
            _helperTestController is null ||
            _helperTestController.State != HelperTestState.Idle ||
            SelectedProfile is not SupervisorProfileConfig profile)
        {
            UpdateHelperTestButton();
            return;
        }

        string profileId = profile.ProfileId;
        _helperTestAvailabilityRefreshPending = true;

        try
        {
            bool canStart = await _helperTestController.CanStartAsync(profileId);

            if (!IsDisposed &&
                SelectedProfile is SupervisorProfileConfig selected &&
                string.Equals(selected.ProfileId, profileId, StringComparison.Ordinal))
            {
                _helperTestCanStart = canStart;
            }
        }
        catch (OperationCanceledException)
        {
            _helperTestCanStart = false;
        }
        catch (ObjectDisposedException)
        {
            _helperTestCanStart = false;
        }
        finally
        {
            _helperTestAvailabilityRefreshPending = false;

            if (!IsDisposed)
                UpdateHelperTestButton();
        }
    }

    /// <summary>Refreshes active-profile availability twice per second.</summary>
    private void HelperTestAvailabilityTimerTick(object? sender, EventArgs e)
    {
        BeginRefreshHelperTestAvailability();
    }

    /// <summary>Marshals controller state changes back to the editor thread.</summary>
    private void HelperTestControllerStateChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || Disposing)
            return;

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(UpdateHelperTestStateFromController));
            }
            catch (InvalidOperationException)
            {
            }

            return;
        }

        UpdateHelperTestStateFromController();
    }

    /// <summary>Applies one controller phase and refreshes availability after returning to idle.</summary>
    private void UpdateHelperTestStateFromController()
    {
        UpdateHelperTestButton();

        if (_helperTestController?.State == HelperTestState.Idle)
            BeginRefreshHelperTestAvailability();
    }

    /// <summary>Maps the test phase and selected profile availability to button text and enabled state.</summary>
    private void UpdateHelperTestButton()
    {
        if (_testHelperButton is null || _testHelperButton.IsDisposed)
            return;

        HelperTestState state = _helperTestController?.State ?? HelperTestState.Idle;
        _testHelperButton.Text = state switch
        {
            HelperTestState.Starting => "Starting test...",
            HelperTestState.Running => "Stop test",
            HelperTestState.Stopping => "Stopping test...",
            _ => "Test helper"
        };
        _testHelperButton.Enabled = state switch
        {
            HelperTestState.Running => true,
            HelperTestState.Idle => _helperTestController is not null &&
                SelectedApplication is not null &&
                SelectedProfile is not null &&
                _helperTestCanStart,
            _ => false
        };
    }

    /// <summary>Defers an accepted editor close until the test helper has finished closing.</summary>
    private bool DelayCloseForHelperTest(FormClosingEventArgs e)
    {
        if (_helperTestCloseReady || _helperTestController is null ||
            _helperTestController.State == HelperTestState.Idle)
        {
            return false;
        }

        e.Cancel = true;

        if (!_helperTestClosePending)
        {
            _helperTestClosePending = true;
            UpdateHelperTestButton();
            _ = StopHelperTestBeforeCloseAsync();
        }

        return true;
    }

    /// <summary>Waits for normal helper deactivation, then retries the already-approved form close.</summary>
    private async Task StopHelperTestBeforeCloseAsync()
    {
        try
        {
            if (_helperTestController is not null)
                await _helperTestController.StopAsync();

            _helperTestCloseReady = true;
            Close();
        }
        catch (Exception exception)
        {
            _helperTestClosePending = false;
            UpdateHelperTestButton();
            MessageBox.Show(
                this,
                $"The test helper is still running, so the editor will remain open.\n\n{exception.Message}",
                "Test helper could not close",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }
    }

    /// <summary>Stops a test before complete supervisor shutdown without showing an editor-owned prompt.</summary>
    internal async Task StopHelperTestForSupervisorExitAsync()
    {
        _closeWithoutUnsavedChangesPrompt = true;

        if (_helperTestController is null ||
            _helperTestController.State == HelperTestState.Idle)
        {
            return;
        }

        try
        {
            await _helperTestController.StopAsync();
        }
        finally
        {
            _helperTestCloseReady = true;
        }
    }

    /// <summary>Stops polling and removes the controller subscription during form disposal.</summary>
    private void DisposeHelperTesting()
    {
        if (_helperTestingDisposed)
            return;

        _helperTestingDisposed = true;
        _helperTestAvailabilityTimer.Stop();
        _helperTestAvailabilityTimer.Tick -= HelperTestAvailabilityTimerTick;
        _helperTestAvailabilityTimer.Dispose();

        if (_helperTestController is not null)
            _helperTestController.StateChanged -= HelperTestControllerStateChanged;
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
                HealthCheckFactory.CreateOneShotActivationCondition(healthCheck);

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

            string runtimePath = JavaLauncherDetector.ResolveRuntimePath(application.Path);
            IReadOnlySet<int> ownerProcessIds =
                ProcessPathDiscovery.FindRunningProcessIds(
                    runtimePath,
                    useSharedCache: false
                );
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
