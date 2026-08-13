namespace AppSupervisor;

/// <summary>Contains global Home Assistant endpoint and bearer-token authentication settings.</summary>
public sealed class HomeAssistantIntegrationConfig
{
    /// <summary>Gets or sets the absolute Home Assistant base URL.</summary>
    public string Url { get; set; } = "";

    /// <summary>Gets or sets the long-lived Home Assistant access token.</summary>
    public string Token { get; set; } = "";
}
