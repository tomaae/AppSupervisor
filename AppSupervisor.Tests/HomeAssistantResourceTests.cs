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

        Assert.True(SpinWait.SpinUntil(resource.IsStarted, TimeSpan.FromSeconds(2)));
        Assert.Equal(["switch.turn_on:switch.test"], client.Calls);
        resource.Deactivate();
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

        Assert.True(SpinWait.SpinUntil(resource.IsStarted, TimeSpan.FromSeconds(2)));
        Assert.True(client.StateQueries > 0);
        Assert.Equal("off", client.State);
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
        Assert.True(SpinWait.SpinUntil(resource.IsStarted, TimeSpan.FromSeconds(2)));
        client.State = "off";
        time.Advance(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(1)));

        Assert.Equal(ManagedResourceUpdate.None, resource.Supervise());
        Assert.True(SpinWait.SpinUntil(
            () => client.Calls.Count == 2,
            TimeSpan.FromSeconds(2)
        ));

        Assert.Equal(ManagedResourceUpdate.Restarted, resource.Supervise());
        Assert.Equal("on", client.State);
    }

    private static HomeAssistantResource CreateResource(
        FakeHomeAssistantClient client,
        string service,
        string entityId = "switch.test",
        bool verify = false,
        bool persistent = false,
        TimeProvider? timeProvider = null)
    {
        return new HomeAssistantResource(
            new HomeAssistantResourceConfig
            {
                Service = service,
                EntityId = entityId,
                EntityName = "Test entity",
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

        public string State { get; set; } = "off";

        public int StateQueries { get; private set; }

        public Task CallServiceAsync(
            string service,
            string entityId,
            CancellationToken cancellationToken)
        {
            Calls.Add($"{service}:{entityId}");

            if (service.EndsWith(".turn_on", StringComparison.Ordinal))
                State = "on";
            else if (service.EndsWith(".turn_off", StringComparison.Ordinal))
                State = "off";

            return Task.CompletedTask;
        }

        public Task<string> GetEntityStateAsync(
            string entityId,
            CancellationToken cancellationToken)
        {
            StateQueries++;
            return Task.FromResult(State);
        }

        public void Dispose() { }
    }

    private sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _utcNow = initial;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }
}
