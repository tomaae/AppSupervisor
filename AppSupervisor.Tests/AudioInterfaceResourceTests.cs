using AppSupervisor.Resources;
using AppSupervisor.WindowsAudio;
using AppSupervisor.Core;

namespace AppSupervisor.Tests;

/// <summary>Verifies Windows audio endpoint apply and restoration lifecycle behavior.</summary>
public sealed class AudioInterfaceResourceTests
{
    [Fact]
    public void ActivateThenDeactivate_IdChanges_RestoresOriginalStateOnReplacementEndpoint()
    {
        AudioInterfaceResourceConfig configuration = CreateConfiguration(restore: true);
        var oldEndpoint = Endpoint("old-endpoint", "old-instance");
        var controller = new FakeAudioController(
            [oldEndpoint],
            new AudioEndpointState(0.42f, true)
        );
        using var resource = new AudioInterfaceResource(configuration, controller);

        resource.Activate();

        Assert.True(resource.IsStarted());
        Assert.Equal(new AudioEndpointState(0.75f, false), controller.LastSetState);

        var replacement = Endpoint("new-endpoint", "new-instance");
        controller.Endpoints = [replacement];
        resource.Deactivate();

        Assert.False(resource.DeactivationPending);
        Assert.Equal("new-endpoint", controller.LastSetEndpointId);
        Assert.Equal(new AudioEndpointState(0.42f, true), controller.LastSetState);
        Assert.Equal("new-endpoint", configuration.EndpointId);
    }

    [Fact]
    public void Deactivate_RestoreDisabled_LeavesRequestedStateInPlace()
    {
        AudioInterfaceResourceConfig configuration = CreateConfiguration(restore: false);
        var controller = new FakeAudioController(
            [Endpoint("endpoint", "instance")],
            new AudioEndpointState(0.25f, true)
        );
        using var resource = new AudioInterfaceResource(configuration, controller);

        resource.Activate();
        resource.Deactivate();

        Assert.Equal(1, controller.SetCount);
        Assert.Equal(new AudioEndpointState(0.75f, false), controller.LastSetState);
        Assert.False(resource.DeactivationPending);
    }

    [Fact]
    public void Deactivate_DefaultChanged_RestoresEndpointModifiedDuringActivation()
    {
        AudioEndpointSnapshot originalPhysical = Endpoint("original", "original-instance");
        AudioEndpointSnapshot originalDefault = originalPhysical with { FollowsDefault = true };
        AudioInterfaceResourceConfig configuration = CreateConfiguration(restore: true);
        configuration.UseDefaultDevice = true;
        var controller = new FakeAudioController(
            [originalDefault, originalPhysical],
            new AudioEndpointState(0.42f, true)
        );
        using var resource = new AudioInterfaceResource(configuration, controller);

        resource.Activate();

        AudioEndpointSnapshot replacementPhysical = new(
            "replacement",
            "replacement-instance",
            "replacement-container",
            "Replacement speakers",
            "Replacement audio",
            AudioInterfaceDirection.Output
        );
        controller.Endpoints =
        [
            replacementPhysical with { FollowsDefault = true },
            replacementPhysical,
            originalPhysical
        ];
        resource.Deactivate();

        Assert.Equal("original", controller.LastSetEndpointId);
        Assert.Equal(new AudioEndpointState(0.42f, true), controller.LastSetState);
    }

    [Fact]
    public void ApplyFailure_StopsAfterFiveAttemptsAndNewActivationResetsBudget()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var controller = new FakeAudioController(
            [Endpoint("endpoint", "instance")],
            new AudioEndpointState(0.25f, true)
        )
        {
            FailSet = true
        };
        using var resource = new AudioInterfaceResource(
            CreateConfiguration(restore: false),
            controller,
            time
        );
        var errors = new List<string>();
        resource.ErrorOccurred += (_, message) => errors.Add(message);

        resource.Activate();
        for (int attempt = 2; attempt <= AutomaticRecoveryBudget.MaximumAttempts; attempt++)
        {
            time.Advance(AutomaticRecoveryBudget.RetryDelay);
            resource.Supervise();
        }

        time.Advance(TimeSpan.FromMinutes(1));
        resource.Supervise();
        Assert.Equal(5, controller.SetCount);
        Assert.Contains("attempt 5 of 5", errors.Last());

        controller.FailSet = false;
        resource.Activate();

        Assert.True(resource.IsStarted());
        Assert.Equal(6, controller.SetCount);
    }

    private static AudioInterfaceResourceConfig CreateConfiguration(bool restore) => new()
    {
        EndpointId = "old-endpoint",
        DeviceInstanceId = "old-instance",
        ContainerId = "fdd0476b-4e83-4e0a-a597-08417ef12dbc",
        FriendlyName = "USB Speakers",
        InterfaceName = "USB Audio",
        Direction = AudioInterfaceDirection.Output,
        VolumePercent = 75,
        Muted = false,
        RestoreOnDeactivate = restore
    };

    private static AudioEndpointSnapshot Endpoint(string endpointId, string instanceId) => new(
        endpointId,
        instanceId,
        "fdd0476b-4e83-4e0a-a597-08417ef12dbc",
        "USB Speakers",
        "USB Audio",
        AudioInterfaceDirection.Output
    );

    private sealed class FakeAudioController(
        IReadOnlyList<AudioEndpointSnapshot> endpoints,
        AudioEndpointState initialState) : IWindowsAudioController
    {
        public IReadOnlyList<AudioEndpointSnapshot> Endpoints { get; set; } = endpoints;

        public int SetCount { get; private set; }

        public string? LastSetEndpointId { get; private set; }

        public AudioEndpointState? LastSetState { get; private set; }

        public bool FailSet { get; set; }

        public IReadOnlyList<AudioEndpointSnapshot> GetActiveEndpoints() => Endpoints;

        public AudioEndpointSnapshot ResolveEndpoint(AudioInterfaceResourceConfig configuration)
        {
            AudioEndpointSnapshot endpoint = configuration.UseDefaultDevice
                ? Endpoints.Single(candidate =>
                    candidate.FollowsDefault &&
                    candidate.Direction == configuration.Direction)
                : WindowsAudioEndpointResolver.Resolve(configuration, Endpoints);
            endpoint.CopyIdentityTo(configuration);
            return endpoint;
        }

        public AudioEndpointState GetState(string endpointId) => initialState;

        public void SetState(string endpointId, AudioEndpointState state)
        {
            SetCount++;

            if (FailSet)
                throw new InvalidOperationException("Audio endpoint rejected the change.");

            LastSetEndpointId = endpointId;
            LastSetState = state;
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _utcNow = initial;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }
}
