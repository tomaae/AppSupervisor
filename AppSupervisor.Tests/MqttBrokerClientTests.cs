using System.Text;
using AppSupervisor.Mqtt;

namespace AppSupervisor.Tests;

/// <summary>Verifies MQTT transaction ordering, exact state matching, and safe errors.</summary>
public sealed class MqttBrokerClientTests
{
    [Fact]
    public async Task PublishAsync_CapturesRetainedStateBeforePublishingAndVerifiesNewState()
    {
        var transport = new FakeTransport();
        transport.OnSubscribe = topic => transport.Receive(topic, "before", retain: true);
        transport.OnPublish = _ => transport.Receive("device/state", "after", retain: false);
        using var client = CreateClient(transport);
        byte[]? captured = null;
        bool accepted = false;

        await client.PublishAsync(
            Message("device/set", "on"),
            new MqttStateCheck("device/state", Bytes("after"), TimeSpan.FromSeconds(1)),
            new MqttRetainedStateCapture("device/state", TimeSpan.FromSeconds(1)),
            payload => captured = payload,
            () => accepted = true,
            CancellationToken.None
        );

        Assert.Equal(["connect", "subscribe:device/state", "publish:device/set", "disconnect"],
            transport.Events);
        Assert.Equal("before", Encoding.UTF8.GetString(Assert.IsType<byte[]>(captured)));
        Assert.True(accepted);
    }

    [Fact]
    public async Task PublishAsync_DoesNotAcceptStaleRetainedMessageAsPostPublishVerification()
    {
        var transport = new FakeTransport();
        transport.OnPublish = _ => transport.Receive(
            "device/state",
            "expected",
            retain: true
        );
        using var client = CreateClient(transport);

        await Assert.ThrowsAsync<TimeoutException>(() => client.PublishAsync(
            Message("device/set", "on"),
            new MqttStateCheck("device/state", Bytes("expected"), TimeSpan.FromMilliseconds(25)),
            capture: null,
            stateCaptured: null,
            publishAccepted: null,
            CancellationToken.None
        ));
    }

    [Fact]
    public async Task PublishAsync_RetainedCaptureRequiresRetainFlag()
    {
        var transport = new FakeTransport();
        transport.OnSubscribe = topic => transport.Receive(topic, "state", retain: false);
        using var client = CreateClient(transport);

        await Assert.ThrowsAsync<TimeoutException>(() => client.PublishAsync(
            Message("device/set", "on"),
            verification: null,
            new MqttRetainedStateCapture("device/state", TimeSpan.FromMilliseconds(25)),
            stateCaptured: null,
            publishAccepted: null,
            CancellationToken.None
        ));

        Assert.DoesNotContain(transport.Events, item => item.StartsWith("publish:"));
    }

    [Fact]
    public async Task TestConnectionAsync_RedactsConfiguredPasswordFromFailure()
    {
        var transport = new FakeTransport { ConnectFailure = new("secret rejected") };
        using var client = new MqttBrokerClient(
            new MqttIntegrationConfig
            {
                Host = "broker.example",
                Port = 8883,
                Password = "secret"
            },
            () => transport
        );

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.TestConnectionAsync(CancellationToken.None)
        );

        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
        Assert.Contains("[redacted]", exception.Message, StringComparison.Ordinal);
    }

    private static MqttBrokerClient CreateClient(FakeTransport transport) => new(
        new MqttIntegrationConfig { Host = "broker.example" },
        () => transport
    );

    private static MqttPublishMessage Message(string topic, string payload) => new(
        topic,
        Bytes(payload),
        MqttQualityOfService.AtLeastOnce,
        Retain: false
    );

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private sealed class FakeTransport : IMqttTransport
    {
        private long _sequence;
        public event Action<MqttReceivedMessage>? MessageReceived;
        public bool IsConnected { get; private set; }
        public long LastReceivedSequence => _sequence;
        public List<string> Events { get; } = [];
        public Action<string>? OnSubscribe { get; set; }
        public Action<MqttPublishMessage>? OnPublish { get; set; }
        public InvalidOperationException? ConnectFailure { get; set; }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("connect");

            if (ConnectFailure is not null)
                throw ConnectFailure;

            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task SubscribeAsync(string topic, CancellationToken cancellationToken)
        {
            Events.Add($"subscribe:{topic}");
            OnSubscribe?.Invoke(topic);
            return Task.CompletedTask;
        }

        public Task PublishAsync(MqttPublishMessage message, CancellationToken cancellationToken)
        {
            Events.Add($"publish:{message.Topic}");
            OnPublish?.Invoke(message);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            Events.Add("disconnect");
            IsConnected = false;
            return Task.CompletedTask;
        }

        public void Receive(string topic, string payload, bool retain)
        {
            MessageReceived?.Invoke(new MqttReceivedMessage(
                Interlocked.Increment(ref _sequence),
                topic,
                Bytes(payload),
                retain
            ));
        }

        public void Dispose() { }
    }
}
