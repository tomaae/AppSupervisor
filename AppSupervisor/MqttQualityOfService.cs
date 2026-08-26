namespace AppSupervisor;

/// <summary>Identifies the delivery guarantee requested for an MQTT publish.</summary>
public enum MqttQualityOfService
{
    /// <summary>Requests best-effort delivery with no acknowledgement.</summary>
    AtMostOnce,

    /// <summary>Requests acknowledged delivery one or more times.</summary>
    AtLeastOnce,

    /// <summary>Requests the MQTT exactly-once handshake.</summary>
    ExactlyOnce
}
