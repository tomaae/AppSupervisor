namespace AppSupervisor.StreamDeck;

/// <summary>Discovers and invokes actions exposed by Stream Deck's official MCP integration.</summary>
internal interface IStreamDeckMcpClient
{
    Task<IReadOnlyList<StreamDeckMcpAction>> LoadActionsAsync(
        CancellationToken cancellationToken);

    Task ExecuteActionAsync(
        StreamDeckResourceConfig configuration,
        CancellationToken cancellationToken);
}
