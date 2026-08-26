namespace AppSupervisor;

/// <summary>Configures one MQTT publish governed by a supervisor profile.</summary>
public sealed class MqttResourceConfig : ManagedResourceConfig
{
    /// <summary>Gets or sets the activation publish topic.</summary>
    public string Topic { get; set; } = "";

    /// <summary>Gets or sets the UTF-8 activation payload. Empty payloads are supported.</summary>
    public string Payload { get; set; } = "";

    /// <summary>Gets or sets the activation delivery guarantee.</summary>
    public MqttQualityOfService Qos { get; set; } = MqttQualityOfService.AtLeastOnce;

    /// <summary>Gets or sets whether the broker retains the activation payload.</summary>
    public bool Retain { get; set; }

    /// <summary>Gets or sets whether activation waits for the expected state payload.</summary>
    public bool VerifyStateChange { get; set; }

    /// <summary>Gets or sets the exact state topic used for capture and verification.</summary>
    public string VerificationTopic { get; set; } = "";

    /// <summary>Gets or sets the exact UTF-8 state payload expected after activation.</summary>
    public string ExpectedState { get; set; } = "";

    /// <summary>Gets or sets the capture or verification timeout in seconds.</summary>
    public int VerificationTimeoutSeconds { get; set; } = 5;

    /// <summary>Gets or sets the inverse behavior used at profile deactivation.</summary>
    public MqttDeactivationBehavior DeactivationBehavior { get; set; }

    /// <summary>Gets or sets the topic used for an explicit or captured-state reverse publish.</summary>
    public string DeactivationTopic { get; set; } = "";

    /// <summary>Gets or sets the UTF-8 payload used by the configured-payload inverse.</summary>
    public string DeactivationPayload { get; set; } = "";

    /// <summary>Gets or sets the reverse publish delivery guarantee.</summary>
    public MqttQualityOfService DeactivationQos { get; set; } = MqttQualityOfService.AtLeastOnce;

    /// <summary>Gets or sets whether the broker retains the reverse payload.</summary>
    public bool DeactivationRetain { get; set; }

    /// <summary>Gets or sets whether a configured-payload inverse waits for its expected state.</summary>
    public bool VerifyDeactivation { get; set; }

    /// <summary>Gets or sets the exact UTF-8 state expected after a configured-payload inverse.</summary>
    public string DeactivationExpectedState { get; set; } = "";

    /// <summary>Gets or sets resource-specific notification targets.</summary>
    public NotificationConfig Notifications { get; set; } = new();
}
