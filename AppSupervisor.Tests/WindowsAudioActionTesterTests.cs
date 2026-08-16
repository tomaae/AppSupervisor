using AppSupervisor.WindowsAudio;

namespace AppSupervisor.Tests;

/// <summary>Verifies temporary audio tests restore state independently of profile options.</summary>
public sealed class WindowsAudioActionTesterTests
{
    [Fact]
    public async Task RunAsync_AppliesRequestedStateThenRestoresOriginalState()
    {
        var endpoint = new AudioEndpointSnapshot(
            "endpoint",
            "instance",
            "container",
            "Speakers",
            "USB Audio",
            AudioInterfaceDirection.Output
        );
        var controller = new FakeController(endpoint, new AudioEndpointState(0.31f, true));
        var configuration = new AudioInterfaceResourceConfig
        {
            EndpointId = endpoint.EndpointId,
            DeviceInstanceId = endpoint.DeviceInstanceId,
            ContainerId = endpoint.ContainerId,
            FriendlyName = endpoint.FriendlyName,
            InterfaceName = endpoint.InterfaceName,
            Direction = endpoint.Direction,
            VolumePercent = 82,
            Muted = false,
            RestoreOnDeactivate = false
        };

        await WindowsAudioActionTester.RunAsync(
            controller,
            configuration,
            TimeSpan.Zero,
            CancellationToken.None
        );

        Assert.Equal(
            [
                new AudioEndpointState(0.82f, false),
                new AudioEndpointState(0.31f, true)
            ],
            controller.States
        );
    }

    private sealed class FakeController(
        AudioEndpointSnapshot endpoint,
        AudioEndpointState originalState) : IWindowsAudioController
    {
        public List<AudioEndpointState> States { get; } = [];

        public IReadOnlyList<AudioEndpointSnapshot> GetActiveEndpoints() => [endpoint];

        public AudioEndpointSnapshot ResolveEndpoint(AudioInterfaceResourceConfig configuration)
        {
            AudioEndpointSnapshot resolved = WindowsAudioEndpointResolver.Resolve(
                configuration,
                [endpoint]
            );
            resolved.CopyIdentityTo(configuration);
            return resolved;
        }

        public AudioEndpointState GetState(string endpointId) => originalState;

        public void SetState(string endpointId, AudioEndpointState state) => States.Add(state);
    }
}
