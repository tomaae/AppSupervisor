using AppSupervisor.Resources;
using AppSupervisor.StreamDeck;

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

    private static StreamDeckResource CreateResource(
        FakeClient client,
        bool isSwitch = false,
        bool restoreSwitch = false) => new(
        new StreamDeckResourceConfig
        {
            ActionId = "4979ce49-d88b-49cb-9a80-1e95eb45d8f9",
            ActionName = "Start VR",
            IsSwitch = isSwitch,
            RestoreSwitchOnDeactivate = restoreSwitch
        },
        client
    );

    private sealed class FakeClient : IStreamDeckMcpClient
    {
        public List<StreamDeckResourceConfig> Actions { get; } = [];

        public Task<IReadOnlyList<StreamDeckMcpAction>> LoadActionsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StreamDeckMcpAction>>([]);

        public Task ExecuteActionAsync(
            StreamDeckResourceConfig configuration,
            CancellationToken cancellationToken)
        {
            Actions.Add(configuration);
            return Task.CompletedTask;
        }
    }
}
