namespace AppSupervisor.Obs;

/// <summary>Executes OBS actions and discovers selectable OBS objects.</summary>
internal interface IObsWebSocketClient : IDisposable
{
    Task ExecuteActionAsync(
        ObsResourceConfig configuration,
        CancellationToken cancellationToken);

    Task<ObsCatalog> LoadCatalogAsync(CancellationToken cancellationToken);
}
