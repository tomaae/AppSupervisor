namespace AppSupervisor.HomeAssistant;

/// <summary>Defines the Home Assistant operations required by runtime resources.</summary>
internal interface IHomeAssistantClient : IDisposable
{
    Task CallServiceAsync(string service, string entityId, CancellationToken cancellationToken);

    Task<string> GetEntityStateAsync(string entityId, CancellationToken cancellationToken);
}
