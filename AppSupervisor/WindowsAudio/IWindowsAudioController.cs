namespace AppSupervisor.WindowsAudio;

/// <summary>Abstracts Windows Core Audio discovery and endpoint volume control.</summary>
internal interface IWindowsAudioController
{
    IReadOnlyList<AudioEndpointSnapshot> GetActiveEndpoints();

    AudioEndpointSnapshot ResolveEndpoint(AudioInterfaceResourceConfig configuration);

    AudioEndpointState GetState(string endpointId);

    void SetState(string endpointId, AudioEndpointState state);
}
