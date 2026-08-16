using AppSupervisor.WindowsAudio;

namespace AppSupervisor.Tests;

/// <summary>Verifies durable Windows audio endpoint identity matching.</summary>
public sealed class WindowsAudioEndpointResolverTests
{
    [Fact]
    public void Resolve_ChangedEndpointId_UsesContainerIdentity()
    {
        var configuration = new AudioInterfaceResourceConfig
        {
            EndpointId = "old-endpoint",
            DeviceInstanceId = "old-instance",
            ContainerId = "4d3cbb97-8c2b-47f3-9227-f6ed9ea01dee",
            FriendlyName = "USB Speakers",
            InterfaceName = "USB Audio",
            Direction = AudioInterfaceDirection.Output
        };
        var replacement = Endpoint(
            "new-endpoint",
            "new-instance",
            configuration.ContainerId,
            "USB Speakers",
            "USB Audio"
        );

        AudioEndpointSnapshot resolved = WindowsAudioEndpointResolver.Resolve(
            configuration,
            [replacement]
        );

        Assert.Same(replacement, resolved);
    }

    [Fact]
    public void Resolve_NoStableIdentity_UsesOnlyUniqueCompositeName()
    {
        var configuration = new AudioInterfaceResourceConfig
        {
            EndpointId = "removed",
            FriendlyName = "Headphones",
            InterfaceName = "Bluetooth Audio",
            Direction = AudioInterfaceDirection.Output
        };
        AudioEndpointSnapshot expected = Endpoint(
            "replacement",
            "instance",
            "container",
            "Headphones",
            "Bluetooth Audio"
        );

        AudioEndpointSnapshot resolved = WindowsAudioEndpointResolver.Resolve(
            configuration,
            [
                expected,
                Endpoint("other", "other-instance", "other-container", "Speakers", "USB Audio")
            ]
        );

        Assert.Same(expected, resolved);
    }

    [Fact]
    public void Resolve_AmbiguousNameOnlyMatch_RequiresReselection()
    {
        var configuration = new AudioInterfaceResourceConfig
        {
            EndpointId = "removed",
            FriendlyName = "Speakers",
            InterfaceName = "USB Audio",
            Direction = AudioInterfaceDirection.Output
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            WindowsAudioEndpointResolver.Resolve(
                configuration,
                [
                    Endpoint("one", "instance-one", "container-one", "Speakers", "USB Audio"),
                    Endpoint("two", "instance-two", "container-two", "Speakers", "USB Audio")
                ]
            )
        );

        Assert.Contains("More than one", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_DefaultAliasAndPhysicalEndpointShareId_SelectsPhysicalEndpoint()
    {
        AudioEndpointSnapshot physical = Endpoint(
            "endpoint",
            "instance",
            "container",
            "Speakers",
            "USB Audio"
        );
        AudioEndpointSnapshot defaultAlias = physical with { FollowsDefault = true };
        var configuration = new AudioInterfaceResourceConfig
        {
            EndpointId = physical.EndpointId,
            FriendlyName = physical.FriendlyName,
            Direction = physical.Direction
        };

        AudioEndpointSnapshot resolved = WindowsAudioEndpointResolver.Resolve(
            configuration,
            [defaultAlias, physical]
        );

        Assert.Same(physical, resolved);
    }

    private static AudioEndpointSnapshot Endpoint(
        string endpointId,
        string instanceId,
        string containerId,
        string friendlyName,
        string interfaceName) =>
        new(
            endpointId,
            instanceId,
            containerId,
            friendlyName,
            interfaceName,
            AudioInterfaceDirection.Output
        );
}
