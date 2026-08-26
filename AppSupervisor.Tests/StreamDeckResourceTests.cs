using AppSupervisor.Resources;
using AppSupervisor.StreamDeck;
using AppSupervisor.Core;

namespace AppSupervisor.Tests;

/// <summary>Verifies Stream Deck actions are activation-only and use the shared MCP client contract.</summary>
public sealed class StreamDeckResourceTests
{
    [Fact]
    public void Activate_ExecutesConfiguredActionOnce()
    {
        var client = new FakeClient();
        using var resource = CreateResource(client);

        resource.Activate();
        resource.AdvanceLifecycle(DateTime.UtcNow);

        Assert.True(SpinWait.SpinUntil(resource.IsStarted, TimeSpan.FromSeconds(2)));
        Assert.Single(client.Actions);
        Assert.Equal("4979ce49-d88b-49cb-9a80-1e95eb45d8f9", client.Actions[0].ActionId);
    }

    [Fact]
    public void Deactivate_DoesNotInvokeAnInverseAction()
    {
        var client = new FakeClient();
        using var resource = CreateResource(client, isSwitch: true, restoreSwitch: false);
        resource.Activate();
        resource.AdvanceLifecycle(DateTime.UtcNow);
        Assert.True(SpinWait.SpinUntil(resource.IsStarted, TimeSpan.FromSeconds(2)));

        resource.Deactivate();
        resource.SuperviseDeactivation();
        resource.AdvanceLifecycle(DateTime.UtcNow);

        Assert.Single(client.Actions);
        Assert.False(resource.DeactivationPending);
    }

    [Fact]
    public void Deactivate_RestorableSwitch_InvokesSwitchAgain()
    {
        var client = new FakeClient();
        using var resource = CreateResource(client, isSwitch: true, restoreSwitch: true);
        resource.Activate();
        resource.AdvanceLifecycle(DateTime.UtcNow);
        Assert.True(SpinWait.SpinUntil(resource.IsStarted, TimeSpan.FromSeconds(2)));

        resource.Deactivate();
        Assert.True(resource.DeactivationPending);
        resource.AdvanceLifecycle(DateTime.UtcNow);

        Assert.True(SpinWait.SpinUntil(
            () => client.Actions.Count == 2 && !resource.DeactivationPending,
            TimeSpan.FromSeconds(2)
        ));
    }

    [Fact]
    public void Deactivate_WhileSwitchActivationIsQueued_AppliesThenRestores()
    {
        var client = new FakeClient();
        using var resource = CreateResource(client, isSwitch: true, restoreSwitch: true);
        resource.Activate();
        resource.Deactivate();

        resource.AdvanceLifecycle(DateTime.UtcNow);
        Assert.True(SpinWait.SpinUntil(
            () => client.Actions.Count == 1 && resource.LifecycleWorkPending,
            TimeSpan.FromSeconds(2)
        ));
        resource.AdvanceLifecycle(DateTime.UtcNow);

        Assert.True(SpinWait.SpinUntil(
            () => client.Actions.Count == 2 && !resource.DeactivationPending,
            TimeSpan.FromSeconds(2)
        ));
        Assert.False(resource.IsStarted());
    }

    [Fact]
    public void FailedAction_StopsAfterFiveDelayedAttempts()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var client = new FakeClient { FailActions = true };
        using var resource = new StreamDeckResource(
            CreateConfiguration(),
            client,
            time
        );
        var errors = new List<string>();
        resource.ErrorOccurred += (_, message) => errors.Add(message);
        resource.Activate();

        for (int attempt = 1; attempt <= AutomaticRecoveryBudget.MaximumAttempts; attempt++)
        {
            resource.AdvanceLifecycle(time.GetUtcNow().UtcDateTime);
            Assert.True(SpinWait.SpinUntil(
                () => client.Attempts == attempt,
                TimeSpan.FromSeconds(2)
            ));
            time.Advance(AutomaticRecoveryBudget.RetryDelay);
        }

        resource.AdvanceLifecycle(time.GetUtcNow().UtcDateTime);
        Assert.Equal(5, client.Attempts);
        Assert.False(resource.LifecycleWorkPending);
        Assert.Contains("attempt 5 of 5", errors.Last());
    }

    private static StreamDeckResource CreateResource(
        FakeClient client,
        bool isSwitch = false,
        bool restoreSwitch = false) => new(
        CreateConfiguration(isSwitch, restoreSwitch),
        client
    );

    private static StreamDeckResourceConfig CreateConfiguration(
        bool isSwitch = false,
        bool restoreSwitch = false) => new()
        {
            ActionId = "4979ce49-d88b-49cb-9a80-1e95eb45d8f9",
            ActionName = "Start VR",
            IsSwitch = isSwitch,
            RestoreSwitchOnDeactivate = restoreSwitch
        };

    private sealed class FakeClient : IStreamDeckMcpClient
    {
        public List<StreamDeckResourceConfig> Actions { get; } = [];

        public int Attempts { get; private set; }

        public bool FailActions { get; set; }

        public Task<IReadOnlyList<StreamDeckMcpAction>> LoadActionsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StreamDeckMcpAction>>([]);

        public Task ExecuteActionAsync(
            StreamDeckResourceConfig configuration,
            CancellationToken cancellationToken)
        {
            Attempts++;

            if (FailActions)
                throw new InvalidOperationException("Stream Deck rejected the action.");

            Actions.Add(configuration);
            return Task.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _utcNow = initial;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }
}
