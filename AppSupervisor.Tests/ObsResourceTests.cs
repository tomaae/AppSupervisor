using AppSupervisor.Core;
using AppSupervisor.Obs;
using AppSupervisor.Resources;

namespace AppSupervisor.Tests;

/// <summary>Verifies that OBS helper-list actions are activation-only and never reversed.</summary>
public sealed class ObsResourceTests
{
    [Fact]
    public void Activate_ExecutesConfiguredActionOnce()
    {
        var client = new FakeObsClient();
        using var resource = CreateResource(client);

        resource.Activate();
        resource.AdvanceLifecycle(DateTime.UtcNow);

        Assert.True(SpinWait.SpinUntil(resource.IsStarted, TimeSpan.FromSeconds(2)));
        Assert.Single(client.Actions);
        Assert.Equal(ObsActionType.SetInputMute, client.Actions[0].Action);
        Assert.True(client.Actions[0].Muted);
    }

    [Fact]
    public void Deactivate_AfterSuccessfulActivation_DoesNotCallObsAgain()
    {
        var client = new FakeObsClient();
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

    [Fact]
    public void Deactivate_BeforeAcceptedActionStarts_AllowsActionToDrainWithoutInverse()
    {
        var client = new FakeObsClient();
        using var resource = CreateResource(client);

        resource.Activate();
        resource.Deactivate();

        Assert.True(resource.DeactivationPending);
        resource.AdvanceLifecycle(DateTime.UtcNow);
        Assert.True(SpinWait.SpinUntil(
            () => !resource.DeactivationPending,
            TimeSpan.FromSeconds(2)
        ));
        Assert.Single(client.Actions);
        Assert.False(resource.IsStarted());
    }

    [Fact]
    public void FailedActivation_RetriesWhileProfileRemainsActive()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var client = new FakeObsClient { FailuresRemaining = 1 };
        using var resource = CreateResource(client, time);
        resource.Activate();
        resource.AdvanceLifecycle(DateTime.UtcNow);
        Assert.True(SpinWait.SpinUntil(
            () => client.Attempts == 1 && !resource.LifecycleWorkPending,
            TimeSpan.FromSeconds(2)
        ));

        time.Advance(TimeSpan.FromSeconds(6));
        resource.Supervise();
        resource.AdvanceLifecycle(DateTime.UtcNow);

        Assert.True(SpinWait.SpinUntil(resource.IsStarted, TimeSpan.FromSeconds(2)));
        Assert.Equal(2, client.Attempts);
        Assert.Single(client.Actions);
    }

    private static ObsResource CreateResource(
        FakeObsClient client,
        TimeProvider? timeProvider = null)
    {
        return new ObsResource(
            new ObsResourceConfig
            {
                Action = ObsActionType.SetInputMute,
                InputName = "Microphone",
                Muted = true
            },
            client,
            timeProvider
        );
    }

    private sealed class FakeObsClient : IObsWebSocketClient
    {
        public List<ObsResourceConfig> Actions { get; } = [];
        public int Attempts { get; private set; }
        public int FailuresRemaining { get; set; }

        public Task ExecuteActionAsync(
            ObsResourceConfig configuration,
            CancellationToken cancellationToken)
        {
            Attempts++;

            if (FailuresRemaining-- > 0)
                throw new InvalidOperationException("Simulated OBS failure.");

            Actions.Add(configuration);
            return Task.CompletedTask;
        }

        public Task<ObsCatalog> LoadCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ObsCatalog("test", [], [], []));

        public void Dispose()
        {
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _utcNow = initial;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }
}
