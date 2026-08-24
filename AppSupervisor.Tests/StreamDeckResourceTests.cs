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
        Assert.Equal("streamdeck__start_vr", client.Actions[0].ToolName);
    }

    [Fact]
    public void Deactivate_DoesNotInvokeAnInverseAction()
    {
        var client = new FakeClient();
        using var resource = CreateResource(client);
        resource.Activate();
        resource.AdvanceLifecycle(DateTime.UtcNow);
        Assert.True(SpinWait.SpinUntil(resource.IsStarted, TimeSpan.FromSeconds(2)));

        resource.Deactivate();
        resource.SuperviseDeactivation();
        resource.AdvanceLifecycle(DateTime.UtcNow);

        Assert.Single(client.Actions);
        Assert.False(resource.DeactivationPending);
    }

    private static StreamDeckResource CreateResource(FakeClient client) => new(
        new StreamDeckResourceConfig
        {
            ToolName = "streamdeck__start_vr",
            ActionName = "Start VR"
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
