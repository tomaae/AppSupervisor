namespace AppSupervisor.StreamDeck;

/// <summary>Describes one argument-free action exposed by Stream Deck through Elgato MCP Server.</summary>
internal sealed record StreamDeckMcpAction(
    string ToolName,
    string DisplayName,
    string Description
);
