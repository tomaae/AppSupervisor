namespace AppSupervisor.Mqtt;

/// <summary>Publishes MQTT messages and optionally captures or verifies exact state payloads.</summary>
internal interface IMqttBrokerClient : IDisposable
{
    Task TestConnectionAsync(CancellationToken cancellationToken);

    Task PublishAsync(
        MqttPublishMessage message,
        MqttStateCheck? verification,
        MqttRetainedStateCapture? capture,
        Action<byte[]>? stateCaptured,
        Action? publishAccepted,
        CancellationToken cancellationToken
    );
}

/// <summary>Contains one exact MQTT message to publish.</summary>
internal sealed record MqttPublishMessage(
    string Topic,
    byte[] Payload,
    MqttQualityOfService Qos,
    bool Retain
);

/// <summary>Contains one exact state payload expected after a publish.</summary>
internal sealed record MqttStateCheck(
    string Topic,
    byte[] ExpectedPayload,
    TimeSpan Timeout
);

/// <summary>Requests the retained pre-activation payload from an exact state topic.</summary>
internal sealed record MqttRetainedStateCapture(string Topic, TimeSpan Timeout);
