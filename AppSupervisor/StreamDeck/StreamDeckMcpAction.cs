namespace AppSupervisor.StreamDeck;

/// <summary>Describes one argument-free action exposed by Stream Deck through Elgato MCP Server.</summary>
internal sealed record StreamDeckMcpAction(
    string ActionId,
    string DisplayName,
    string Description
);
