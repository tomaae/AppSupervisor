using AppSupervisor.ConfigurationUI;
using AppSupervisor.Core;
using AppSupervisor.Notifications;
using AppSupervisor.Resources;

namespace AppSupervisor.Tests;

/// <summary>Verifies the editor helper-test state machine around the production lifecycle contract.</summary>
public sealed class ManagedApplicationTestControllerTests
{
    /// <summary>Confirms startup waits for readiness and stop waits for immediate deactivation.</summary>
    [Fact]
    public async Task StartAndStopAsync_ReadinessAndClosePhases_GateCompletion()
    {
        FakeApplicationLifecycle? lifecycle = null;
        ManagedApplicationConfig? receivedConfiguration = null;
        bool helperRequired = false;

        await using var controller = new ManagedApplicationTestController(
            ExecuteInlineAsync,
            _ => false,
            _ => helperRequired,
            (configuration, shouldRemainRunning) =>
            {
                receivedConfiguration = configuration;
                lifecycle = new FakeApplicationLifecycle(
                    configuration,
                    shouldRemainRunning
                );
                return lifecycle;
            }
        );
        var configuration = new ManagedApplicationConfig
        {
            Path = Path.Combine(Path.GetTempPath(), "helper-test.exe"),
            AppUri = "steam://rungameid/42",
            Arguments = "--preserved",
            LeaveRunningAfterProfileStops = true,
            StartupMacros =
            [
                new StartupMacroActionConfig
                {
                    Type = StartupMacroActionType.Delay,
                    DelayMilliseconds = 250
                }
            ]
        };

        Task startTask = controller.StartAsync("profile-id", configuration);
        await WaitUntilAsync(() => lifecycle?.Activated == true);

        Assert.Equal(HelperTestState.Starting, controller.State);
        Assert.False(startTask.IsCompleted);
        Assert.NotNull(receivedConfiguration);
        Assert.Equal(configuration.AppUri, receivedConfiguration.AppUri);
        Assert.Equal(configuration.Arguments, receivedConfiguration.Arguments);
        Assert.Single(receivedConfiguration.StartupMacros);
        Assert.False(receivedConfiguration.LeaveRunningAfterProfileStops);

        lifecycle!.AllowStart = true;
        await startTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(HelperTestState.Running, controller.State);

        Task stopTask = controller.StopAsync();
        await WaitUntilAsync(() => lifecycle.Deactivated);

        Assert.Equal(HelperTestState.Stopping, controller.State);
        Assert.False(stopTask.IsCompleted);
        Assert.True(lifecycle.RecoveryCancelledBeforeDeactivate);

        lifecycle.AllowClose = true;
        await stopTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(HelperTestState.Idle, controller.State);
        Assert.True(lifecycle.Disposed);
    }

    /// <summary>Confirms a busy production profile disables and rejects test startup.</summary>
    [Fact]
    public async Task CanStartAndStartAsync_ActiveProfile_RejectTest()
    {
        bool factoryCalled = false;
        await using var controller = new ManagedApplicationTestController(
            ExecuteInlineAsync,
            _ => true,
            _ => false,
            (configuration, shouldRemainRunning) =>
            {
                factoryCalled = true;
                return new FakeApplicationLifecycle(configuration, shouldRemainRunning);
            }
        );

        Assert.False(await controller.CanStartAsync("active-profile"));
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.StartAsync(
                "active-profile",
                new ManagedApplicationConfig
                {
                    Path = Path.Combine(Path.GetTempPath(), "busy-helper.exe")
                }
            )
        );

        Assert.Contains("active", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(factoryCalled);
        Assert.Equal(HelperTestState.Idle, controller.State);
    }

    /// <summary>Confirms stop releases test ownership without closing a helper claimed by production.</summary>
    [Fact]
    public async Task StopAsync_HelperBecomesRequired_DoesNotCloseProductionOwnedProcess()
    {
        FakeApplicationLifecycle? lifecycle = null;
        bool helperRequired = false;
        await using var controller = new ManagedApplicationTestController(
            ExecuteInlineAsync,
            _ => false,
            _ => helperRequired,
            (configuration, shouldRemainRunning) =>
            {
                lifecycle = new FakeApplicationLifecycle(
                    configuration,
                    shouldRemainRunning
                )
                {
                    AllowStart = true
                };
                return lifecycle;
            }
        );

        await controller.StartAsync(
            "profile-id",
            new ManagedApplicationConfig
            {
                Path = Path.Combine(Path.GetTempPath(), "shared-helper.exe")
            }
        ).WaitAsync(TimeSpan.FromSeconds(3));
        helperRequired = true;

        await controller.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.NotNull(lifecycle);
        Assert.True(lifecycle.IsRunning());
        Assert.False(lifecycle.CloseRequested);
        Assert.True(lifecycle.Disposed);
        Assert.Equal(HelperTestState.Idle, controller.State);
    }

    private static Task ExecuteInlineAsync(Action operation)
    {
        operation();
        return Task.CompletedTask;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        while (!predicate())
            await Task.Delay(20, timeout.Token);
    }

    /// <summary>Deterministic managed-application contract used to control readiness and close completion.</summary>
    private sealed class FakeApplicationLifecycle : IManagedApplicationLifecycle
    {
        private readonly Func<bool> _shouldRemainRunning;
        private bool _starting;
        private bool _closing;
        private bool _running;
        private bool _recoveryCancelled;

        public FakeApplicationLifecycle(
            ManagedApplicationConfig configuration,
            Func<bool> shouldRemainRunning)
        {
            Config = configuration;
            _shouldRemainRunning = shouldRemainRunning;
        }

        public event Action<IManagedResource, string>? ErrorOccurred
        {
            add { }
            remove { }
        }

        public ManagedApplicationConfig Config { get; }

        public string DisplayName => Path.GetFileName(Config.Path);

        public IReadOnlyList<NotificationTarget> NotificationTargets => [];

        public bool Activated { get; private set; }

        public bool Deactivated { get; private set; }

        public bool RecoveryCancelledBeforeDeactivate { get; private set; }

        public bool CloseRequested { get; private set; }

        public bool Disposed { get; private set; }

        public bool AllowStart { get; set; }

        public bool AllowClose { get; set; }

        public bool CloseOperationPending => _closing;

        public bool LifecycleWorkPending => _starting || _closing;

        public void Activate()
        {
            Activated = true;
            _starting = true;
        }

        public ManagedResourceUpdate Supervise() => ManagedResourceUpdate.None;

        public void CancelPendingRecovery()
        {
            _recoveryCancelled = true;
            _starting = false;
        }

        public void SuspendMonitoring()
        {
        }

        public void Deactivate()
        {
            Deactivated = true;
            RecoveryCancelledBeforeDeactivate = _recoveryCancelled;

            if (_shouldRemainRunning())
                return;

            CloseRequested = true;
            _closing = true;
        }

        public void SuperviseDeactivation()
        {
            AdvanceLifecycle(DateTime.UtcNow);
        }

        public bool IsRunning() => _running;

        public bool IsStarted() => _running && !_starting;

        public ManagedResourceUpdate AdvanceLifecycle(DateTime nowUtc)
        {
            if (_starting && AllowStart)
            {
                _starting = false;
                _running = true;
            }

            if (_closing && AllowClose)
            {
                _closing = false;
                _running = false;
            }

            return ManagedResourceUpdate.None;
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
