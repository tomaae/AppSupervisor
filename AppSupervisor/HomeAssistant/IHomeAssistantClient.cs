namespace AppSupervisor.HomeAssistant;

/// <summary>Defines the Home Assistant operations required by runtime resources.</summary>
internal interface IHomeAssistantClient : IDisposable
{
    Task CallServiceAsync(
        string service,
        string entityId,
        int? brightnessPercent,
        CancellationToken cancellationToken);

    Task<HomeAssistantEntityState> GetEntityStateAsync(
        string entityId,
        CancellationToken cancellationToken);
}

/// <summary>Contains the state and normalized light brightness returned by Home Assistant.</summary>
/// <param name="State">The entity state, such as on or off.</param>
/// <param name="BrightnessPercent">The optional light brightness normalized to 0 through 100.</param>
internal sealed record HomeAssistantEntityState(string State, int? BrightnessPercent)
{
    /// <summary>Checks both the requested state and any requested light brightness.</summary>
    public bool Matches(string expectedState, int? expectedBrightnessPercent) =>
        string.Equals(State, expectedState, StringComparison.OrdinalIgnoreCase) &&
        (expectedBrightnessPercent is null || BrightnessPercent == expectedBrightnessPercent);
}
