namespace AppSupervisor;

/// <summary>Contains the shared TCP MQTT broker endpoint and authentication settings.</summary>
public sealed class MqttIntegrationConfig
{
    /// <summary>Gets or sets the broker DNS name or IP address, without a URI scheme.</summary>
    public string Host { get; set; } = "";

    /// <summary>Gets or sets the broker TCP port.</summary>
    public int Port { get; set; } = 1883;

    /// <summary>Gets or sets whether the broker connection uses TLS with normal OS certificate validation.</summary>
    public bool UseTls { get; set; }

    /// <summary>Gets or sets the optional MQTT user name.</summary>
    public string Username { get; set; } = "";

    /// <summary>Gets or sets the optional MQTT password.</summary>
    public string Password { get; set; } = "";
}
