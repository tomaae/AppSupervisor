using AppSupervisor.Core;
using AppSupervisor.HomeAssistant;
using AppSupervisor.Resources;

namespace AppSupervisor.Tests;

/// <summary>Verifies asynchronous Home Assistant activation, reversal, verification, and persistence.</summary>
public sealed class HomeAssistantResourceTests
{
    [Fact]
    public void ActivateAndDeactivate_TurnOnAction_CallsDeterministicInverse()
    {
        var client = new FakeHomeAssistantClient();
        using var resource = CreateResource(client, service: "switch.turn_on");

        resource.Activate();
        resource.AdvanceLifecycle(DateTime.UtcNow);

        Assert.True(SpinWait.SpinUntil(resource.IsStarted, TimeSpan.FromSeconds(2)));
        Assert.Equal(["switch.turn_on:switch.test"], client.Calls);
        resource.Deactivate();
        resource.AdvanceLifecycle(DateTime.UtcNow);
        Assert.True(SpinWait.SpinUntil(
            () => client.Calls.Count == 2,
            TimeSpan.FromSeconds(2)
        ));
        Assert.Equal("switch.turn_off:switch.test", client.Calls[1]);
    }

    [Fact]
    public void Activate_WithVerification_QueriesRequestedState()
    {
        var client = new FakeHomeAssistantClient();
        using var resource = CreateResource(
            client,
            service: "switch.turn_off",
            verify: true
        );

        resource.Activate();
        resource.AdvanceLifecycle(DateTime.UtcNow);

        Assert.True(SpinWait.SpinUntil(resource.IsStarted, TimeSpan.FromSeconds(2)));
        Assert.True(client.StateQueries > 0);
        Assert.Equal("off", client.State);
    }

    [Fact]
    public void Activate_LightTurnOn_PassesConfiguredBrightness()
    {
        var client = new FakeHomeAssistantClient();
        using var resource = CreateResource(
            client,
            service: "light.turn_on",
            entityId: "light.test",
            brightnessPercent: 42
        );

        resource.Activate();
        resource.AdvanceLifecycle(DateTime.UtcNow);

        Assert.True(SpinWait.SpinUntil(resource.IsStarted, TimeSpan.FromSeconds(2)));
        Assert.Equal([42], client.BrightnessPercentages);
    }

    [Fact]
    public void Activate_OlderLightTurnOnConfig_DefaultsBrightnessTo100()
    {
        var client = new FakeHomeAssistantClient();
        using var resource = CreateResource(
            client,
            service: "light.turn_on",
            entityId: "light.test"
        );

        resource.Activate();
        resource.AdvanceLifecycle(DateTime.UtcNow);

        Assert.True(SpinWait.SpinUntil(resource.IsStarted, TimeSpan.FromSeconds(2)));
        Assert.Equal([100], client.BrightnessPercentages);
    }

    [Fact]
    public void Deactivate_ButtonPress_DoesNotPressStatelessButtonAgain()
    {
        var client = new FakeHomeAssistantClient();
        using var resource = CreateResource(
            client,
            service: "button.press",
            entityId: "button.test"
        );

        resource.Activate();
        resource.AdvanceLifecycle(DateTime.UtcNow);
        Assert.True(SpinWait.SpinUntil(resource.IsStarted, TimeSpan.FromSeconds(2)));
        resource.Deactivate();

        Assert.Equal(["button.press:button.test"], client.Calls);
    }

    [Fact]
    public void Supervise_PersistentStateChanged_RestoresStateAfterOneMinute()
    {
        var client = new FakeHomeAssistantClient();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var resource = CreateResource(
            client,
            service: "switch.turn_on",
            persistent: true,
            timeProvider: time
        );
        resource.Activate();
        resource.AdvanceLifecycle(DateTime.UtcNow);
        Assert.True(SpinWait.SpinUntil(resource.IsStarted, TimeSpan.FromSeconds(2)));
        client.State = "off";
        time.Advance(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(1)));

        Assert.Equal(ManagedResourceUpdate.None, resource.Supervise());
        resource.AdvanceLifecycle(DateTime.UtcNow);
        Assert.True(SpinWait.SpinUntil(
            () => client.Calls.Count == 2,
            TimeSpan.FromSeconds(2)
        ));

        Assert.Equal(ManagedResourceUpdate.Restarted, resource.Supervise());
        Assert.Equal("on", client.State);
    }

    [Fact]
    public void Supervise_PersistentLightBrightnessChanged_ReappliesConfiguredBrightness()
    {
        var client = new FakeHomeAssistantClient();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var resource = CreateResource(
            client,
            service: "light.turn_on",
            entityId: "light.test",
            persistent: true,
            brightnessPercent: 35,
            timeProvider: time
        );
        resource.Activate();
        resource.AdvanceLifecycle(DateTime.UtcNow);
        Assert.True(SpinWait.SpinUntil(resource.IsStarted, TimeSpan.FromSeconds(2)));
        client.BrightnessPercent = 70;
        time.Advance(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(1)));

        Assert.Equal(ManagedResourceUpdate.None, resource.Supervise());
        resource.AdvanceLifecycle(DateTime.UtcNow);
        Assert.True(SpinWait.SpinUntil(
            () => client.Calls.Count == 2,
            TimeSpan.FromSeconds(2)
        ));

        Assert.Equal(ManagedResourceUpdate.Restarted, resource.Supervise());
        Assert.Equal([35, 35], client.BrightnessPercentages);
        Assert.Equal(35, client.BrightnessPercent);
    }

    [Fact]
    public void ActivateDeactivateActivate_LifecyclePreservesAcceptedOrder()
    {
        var client = new FakeHomeAssistantClient();
        using var resource = CreateResource(client, service: "switch.turn_on");

        resource.Activate();
        resource.Deactivate();
        resource.Activate();

        resource.AdvanceLifecycle(DateTime.UtcNow);
        resource.AdvanceLifecycle(DateTime.UtcNow);
        resource.AdvanceLifecycle(DateTime.UtcNow);

        Assert.Equal(
            [
                "switch.turn_on:switch.test",
                "switch.turn_off:switch.test",
                "switch.turn_on:switch.test"
            ],
            client.Calls
        );
        Assert.True(resource.IsStarted());
    }

    [Fact]
    public void BeginPauseDrain_AcceptedActivationRemainsPendingUntilItFinishes()
    {
        var client = new BlockingHomeAssistantClient();
        using var resource = new HomeAssistantResource(
            new HomeAssistantResourceConfig
            {
                Service = "switch.turn_on",
                EntityId = "switch.test"
            },
            client
        );

        resource.Activate();
        resource.BeginPauseDrain();

        Assert.True(resource.PauseDrainPending);
        resource.AdvanceLifecycle(DateTime.UtcNow);
        Assert.True(resource.PauseDrainPending);
        client.Complete();
        Assert.True(SpinWait.SpinUntil(
            () => !resource.PauseDrainPending,
            TimeSpan.FromSeconds(2)
        ));
        Assert.True(resource.IsStarted());
    }

    [Fact]
    public void ActivationFailure_StopsAfterFiveAttemptsAndNewLifecycleResetsBudget()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var client = new FakeHomeAssistantClient { FailCalls = true };
        using var resource = CreateResource(
            client,
            service: "switch.turn_on",
            timeProvider: time
        );
        var errors = new List<string>();
        resource.ErrorOccurred += (_, message) => errors.Add(message);
        resource.Activate();

        for (int attempt = 1; attempt <= AutomaticRecoveryBudget.MaximumAttempts; attempt++)
        {
            resource.AdvanceLifecycle(time.GetUtcNow().UtcDateTime);
            Assert.True(SpinWait.SpinUntil(
                () => client.Calls.Count == attempt,
                TimeSpan.FromSeconds(2)
            ));
            time.Advance(AutomaticRecoveryBudget.RetryDelay);
        }

        resource.AdvanceLifecycle(time.GetUtcNow().UtcDateTime);
        Assert.Equal(5, client.Calls.Count);
        Assert.False(resource.LifecycleWorkPending);
        Assert.Contains("attempt 5 of 5", errors.Last());

        client.FailCalls = false;
        resource.Activate();
        resource.AdvanceLifecycle(time.GetUtcNow().UtcDateTime);

        Assert.True(SpinWait.SpinUntil(resource.IsStarted, TimeSpan.FromSeconds(2)));
        Assert.Equal(6, client.Calls.Count);
    }

    private static HomeAssistantResource CreateResource(
        FakeHomeAssistantClient client,
        string service,
        string entityId = "switch.test",
        bool verify = false,
        bool persistent = false,
        int? brightnessPercent = null,
        TimeProvider? timeProvider = null)
    {
        return new HomeAssistantResource(
            new HomeAssistantResourceConfig
            {
                Service = service,
                EntityId = entityId,
                EntityName = "Test entity",
                BrightnessPercent = brightnessPercent,
                VerifyStateChange = verify,
                Persistent = persistent
            },
            client,
            timeProvider
        );
    }

    private sealed class FakeHomeAssistantClient : IHomeAssistantClient
    {
        public List<string> Calls { get; } = [];

        public List<int?> BrightnessPercentages { get; } = [];

        public string State { get; set; } = "off";

        public int? BrightnessPercent { get; set; }

        public int StateQueries { get; private set; }

        public bool FailCalls { get; set; }

        public Task CallServiceAsync(
            string service,
            string entityId,
            int? brightnessPercent,
            CancellationToken cancellationToken)
        {
            Calls.Add($"{service}:{entityId}");
            BrightnessPercentages.Add(brightnessPercent);

            if (FailCalls)
                throw new InvalidOperationException("Home Assistant rejected the action.");

            if (service.EndsWith(".turn_on", StringComparison.Ordinal))
            {
                State = "on";
                if (brightnessPercent is not null)
                    BrightnessPercent = brightnessPercent;
            }
            else if (service.EndsWith(".turn_off", StringComparison.Ordinal))
            {
                State = "off";
                BrightnessPercent = null;
            }

            return Task.CompletedTask;
        }

        public Task<HomeAssistantEntityState> GetEntityStateAsync(
            string entityId,
            CancellationToken cancellationToken)
        {
            StateQueries++;
            return Task.FromResult(new HomeAssistantEntityState(State, BrightnessPercent));
        }

        public void Dispose() { }
    }

    private sealed class BlockingHomeAssistantClient : IHomeAssistantClient
    {
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task CallServiceAsync(
            string service,
            string entityId,
            int? brightnessPercent,
            CancellationToken cancellationToken) =>
            _completion.Task.WaitAsync(cancellationToken);

        public Task<HomeAssistantEntityState> GetEntityStateAsync(
            string entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HomeAssistantEntityState("on", null));

        public void Complete() => _completion.TrySetResult();

        public void Dispose() => _completion.TrySetCanceled();
    }

    private sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _utcNow = initial;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }
}
