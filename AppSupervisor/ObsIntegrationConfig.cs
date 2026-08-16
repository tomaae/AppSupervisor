namespace AppSupervisor;

/// <summary>Contains global OBS WebSocket v5 endpoint and authentication settings.</summary>
public sealed class ObsIntegrationConfig
{
    /// <summary>Gets or sets the OBS WebSocket server hostname or IP address.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Gets or sets the OBS WebSocket server port.</summary>
    public int Port { get; set; } = 4455;

    /// <summary>Gets or sets the optional OBS WebSocket server password.</summary>
    public string Password { get; set; } = "";
}
