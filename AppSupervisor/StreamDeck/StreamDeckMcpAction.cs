namespace AppSupervisor.StreamDeck;

/// <summary>Describes one argument-free action exposed by Stream Deck through Elgato MCP Server.</summary>
internal sealed record StreamDeckMcpAction(
    string ActionId,
    string DisplayName,
    string Description,
    string Title,
    bool IsSwitch,
    int CurrentState
)
{
    /// <summary>Gets the unambiguous action name shown by the configuration selector.</summary>
    public string SelectorLabel =>
        $"{(string.IsNullOrWhiteSpace(Title) ? "" : $"{Title} — ")}" +
        $"{DisplayName} ({(IsSwitch ? "switch" : "button")})";
}
