namespace AppSupervisor;

/// <summary>Contains application-wide integrations that are not owned by an individual supervisor profile.</summary>
public sealed class IntegrationsConfig
{
    /// <summary>Gets or sets the minimum severity written to the diagnostic session log.</summary>
    public SupervisorLogLevel LogLevel { get; set; } = SupervisorLogLevel.Info;

    /// <summary>Gets or sets the global read-only Supervisor API.</summary>
    public SupervisorApiConfig SupervisorApi { get; set; } = new();

    /// <summary>Gets or sets global Home Assistant authentication and endpoint settings.</summary>
    public HomeAssistantIntegrationConfig HomeAssistant { get; set; } = new();

    /// <summary>Gets or sets the global MQTT broker connection and authentication settings.</summary>
    public MqttIntegrationConfig Mqtt { get; set; } = new();

    /// <summary>Gets or sets global OBS WebSocket endpoint and authentication settings.</summary>
    public ObsIntegrationConfig Obs { get; set; } = new();

    /// <summary>Gets or sets the global Twitch broadcaster authorization identity.</summary>
    public TwitchIntegrationConfig Twitch { get; set; } = new();

    /// <summary>Gets or sets global Bluetooth device registration and presence timing.</summary>
    public BluetoothIntegrationConfig Bluetooth { get; set; } = new();

    /// <summary>Gets or sets SteamVR tracked-device monitoring.</summary>
    public SteamVrIntegrationConfig SteamVr { get; set; } = new();
}
