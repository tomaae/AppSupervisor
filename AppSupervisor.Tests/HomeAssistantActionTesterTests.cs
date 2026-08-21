using AppSupervisor.HomeAssistant;

namespace AppSupervisor.Tests;

/// <summary>Verifies reversible Home Assistant resource previews without a live server.</summary>
public sealed class HomeAssistantActionTesterTests
{
    /// <summary>Confirms a state-changing action is observed after five seconds and then reversed.</summary>
    [Fact]
    public async Task RunAsync_StateChanges_AppliesAndRestoresOriginalState()
    {
        var client = new FakeHomeAssistantClient("off");
        TimeSpan? requestedDelay = null;

        HomeAssistantActionTestResult result = await HomeAssistantActionTester.RunAsync(
            client,
            "switch.turn_on",
            "switch.test",
            brightnessPercent: null,
            (delay, _) =>
            {
                requestedDelay = delay;
                return Task.CompletedTask;
            },
            CancellationToken.None
        );

        Assert.True(result.Changed);
        Assert.Equal("off", result.OriginalState);
        Assert.Equal("on", result.DesiredState);
        Assert.Equal(TimeSpan.FromSeconds(5), requestedDelay);
        Assert.Equal(
            ["switch.turn_on:switch.test", "switch.turn_off:switch.test"],
            client.Calls
        );
        Assert.Equal("off", client.State);
    }

    /// <summary>Confirms no service is called when the configured action would leave state unchanged.</summary>
    [Fact]
    public async Task RunAsync_StateAlreadyDesired_ReturnsWithoutCallingService()
    {
        var client = new FakeHomeAssistantClient("on");
        bool delayed = false;

        HomeAssistantActionTestResult result = await HomeAssistantActionTester.RunAsync(
            client,
            "switch.turn_on",
            "switch.test",
            brightnessPercent: null,
            (_, _) =>
            {
                delayed = true;
                return Task.CompletedTask;
            },
            CancellationToken.None
        );

        Assert.False(result.Changed);
        Assert.Empty(client.Calls);
        Assert.False(delayed);
    }

    /// <summary>Confirms a failed state transition still requests restoration before reporting failure.</summary>
    [Fact]
    public async Task RunAsync_ActionDoesNotChangeState_RestoresThenFails()
    {
        var client = new FakeHomeAssistantClient("off") { ApplyStateChanges = false };

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => HomeAssistantActionTester.RunAsync(
                client,
                "switch.turn_on",
                "switch.test",
                brightnessPercent: null,
                (_, _) => Task.CompletedTask,
                CancellationToken.None
            )
        );

        Assert.Contains("did not produce the expected 'on' state", exception.Message);
        Assert.Equal(
            ["switch.turn_on:switch.test", "switch.turn_off:switch.test"],
            client.Calls
        );
    }

    /// <summary>Confirms stateless button actions are refused because they cannot be reverted.</summary>
    [Fact]
    public async Task RunAsync_StatelessAction_RefusesUnsafePreview()
    {
        var client = new FakeHomeAssistantClient("unknown");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => HomeAssistantActionTester.RunAsync(
                client,
                "button.press",
                "button.test",
                brightnessPercent: null,
                (_, _) => Task.CompletedTask,
                CancellationToken.None
            )
        );

        Assert.Contains("cannot be tested safely", exception.Message);
        Assert.Empty(client.Calls);
    }

    /// <summary>Passes configured brightness only to the temporary light.turn_on call.</summary>
    [Fact]
    public async Task RunAsync_LightTurnOn_PassesBrightnessToPreviewOnly()
    {
        var client = new FakeHomeAssistantClient("off");

        await HomeAssistantActionTester.RunAsync(
            client,
            "light.turn_on",
            "light.test",
            42,
            (_, _) => Task.CompletedTask,
            CancellationToken.None
        );

        Assert.Equal([42, null], client.BrightnessPercentages);
    }

    /// <summary>Restores an already-on light's original brightness after previewing a new one.</summary>
    [Fact]
    public async Task RunAsync_LightAlreadyOn_RestoresOriginalBrightness()
    {
        var client = new FakeHomeAssistantClient("on", brightnessPercent: 70);

        HomeAssistantActionTestResult result = await HomeAssistantActionTester.RunAsync(
            client,
            "light.turn_on",
            "light.test",
            35,
            (_, _) => Task.CompletedTask,
            CancellationToken.None
        );

        Assert.True(result.Changed);
        Assert.Equal([35, 70], client.BrightnessPercentages);
        Assert.Equal("on", client.State);
        Assert.Equal(70, client.BrightnessPercent);
    }

    /// <summary>Provides deterministic state reads and service effects for action-preview tests.</summary>
    private sealed class FakeHomeAssistantClient : IHomeAssistantClient
    {
        /// <summary>Creates a fake client with a selected initial entity state.</summary>
        /// <param name="state">The initial Home Assistant state.</param>
        public FakeHomeAssistantClient(string state, int? brightnessPercent = null)
        {
            State = state;
            BrightnessPercent = brightnessPercent;
        }

        /// <summary>Gets the ordered service calls received by the fake client.</summary>
        public List<string> Calls { get; } = [];

        /// <summary>Gets the ordered optional brightness values received by service calls.</summary>
        public List<int?> BrightnessPercentages { get; } = [];

        /// <summary>Gets or sets whether turn_on and turn_off calls mutate the fake state.</summary>
        public bool ApplyStateChanges { get; set; } = true;

        /// <summary>Gets the fake entity's current state.</summary>
        public string State { get; private set; }

        /// <summary>Gets the fake light brightness.</summary>
        public int? BrightnessPercent { get; private set; }

        /// <summary>Records a service call and optionally applies its deterministic state.</summary>
        /// <param name="service">The requested Home Assistant service.</param>
        /// <param name="entityId">The requested entity identifier.</param>
        /// <param name="cancellationToken">The operation cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task CallServiceAsync(
            string service,
            string entityId,
            int? brightnessPercent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"{service}:{entityId}");
            BrightnessPercentages.Add(brightnessPercent);

            if (ApplyStateChanges)
            {
                State = HomeAssistantServiceSemantics.GetDesiredState(service) ?? State;

                if (brightnessPercent is not null)
                    BrightnessPercent = brightnessPercent;
                else if (string.Equals(State, "off", StringComparison.OrdinalIgnoreCase))
                    BrightnessPercent = null;
            }

            return Task.CompletedTask;
        }

        /// <summary>Returns the fake entity state.</summary>
        /// <param name="entityId">The ignored fake entity identifier.</param>
        /// <param name="cancellationToken">The operation cancellation token.</param>
        /// <returns>The current fake state.</returns>
        public Task<HomeAssistantEntityState> GetEntityStateAsync(
            string entityId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HomeAssistantEntityState(State, BrightnessPercent));
        }

        /// <summary>Releases the fake client; it owns no resources.</summary>
        public void Dispose()
        {
        }
    }
}
