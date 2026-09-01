using System.Text.Json.Serialization;

namespace AppSupervisor;

/// <summary>Configures one process- or Bluetooth-triggered profile and its ordered managed resources.</summary>
public class SupervisorProfileConfig
{
    /// <summary>Gets or sets the stable internal identifier used by API routes.</summary>
    public string ProfileId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets or sets the unique user-facing profile name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Gets or sets whether the profile participates in supervision.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the optional profile that must be active and running before this trigger is monitored.</summary>
    public string DependencyProfileId { get; set; } = "";

    /// <summary>Gets or sets the kind of condition that activates this profile.</summary>
    public ProfileTriggerType TriggerType { get; set; } = ProfileTriggerType.Process;

    /// <summary>Gets or sets the executable filename whose running state activates the profile.</summary>
    public string MonitorProcess { get; set; } = "";

    /// <summary>Gets or sets the globally registered Bluetooth devices that activate this profile in ANY mode.</summary>
    public List<string> MonitorBluetoothDeviceIds { get; set; } = [];

    /// <summary>Receives the retired singular JSON property during backward-compatible loading.</summary>
    [JsonPropertyName("monitorBluetoothDeviceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyMonitorBluetoothDeviceId { get; set; }

    /// <summary>Gets or sets the first selected device for source compatibility with older callers.</summary>
    [JsonIgnore]
    public string MonitorBluetoothDeviceId
    {
        get => MonitorBluetoothDeviceIds?.FirstOrDefault() ??
            LegacyMonitorBluetoothDeviceId ?? "";
        set
        {
            MonitorBluetoothDeviceIds = string.IsNullOrWhiteSpace(value) ? [] : [value];
            LegacyMonitorBluetoothDeviceId = null;
        }
    }

    /// <summary>Gets or sets how long the selected trigger may remain absent before resources are closed.</summary>
    public int? CloseTimeoutSeconds { get; set; }

    /// <summary>Gets or sets how long an unexpectedly absent resource may remain absent before restart.</summary>
    public int? RestartTimeoutSeconds { get; set; }

    /// <summary>Gets or sets the helper applications supervised by this profile.</summary>
    public List<ManagedApplicationConfig> Applications { get; set; } = [];

    /// <summary>Gets or sets the Windows services supervised by this profile.</summary>
    public List<ManagedServiceConfig> Services { get; set; } = [];

    /// <summary>Gets or sets explicit nonblocking delays in the profile's startup sequence.</summary>
    public List<DelayResourceConfig> Delays { get; set; } = [];

    /// <summary>Gets or sets Home Assistant actions supervised by this profile.</summary>
    public List<HomeAssistantResourceConfig> HomeAssistantResources { get; set; } = [];

    /// <summary>Gets or sets MQTT publishes supervised by this profile.</summary>
    public List<MqttResourceConfig> MqttResources { get; set; } = [];

    /// <summary>Gets or sets one-way OBS actions issued when this profile activates.</summary>
    public List<ObsResourceConfig> ObsResources { get; set; } = [];

    /// <summary>Gets or sets Stream Deck actions issued through Elgato's official MCP server.</summary>
    public List<StreamDeckResourceConfig> StreamDeckResources { get; set; } = [];

    /// <summary>Gets or sets Twitch broadcaster actions supervised by this profile.</summary>
    public List<TwitchResourceConfig> TwitchResources { get; set; } = [];

    /// <summary>Gets or sets Windows audio endpoint volume and mute actions.</summary>
    public List<AudioInterfaceResourceConfig> AudioInterfaces { get; set; } = [];
}
