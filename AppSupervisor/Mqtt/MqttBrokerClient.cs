using System.Buffers;
using System.Security.Cryptography;
using System.Threading.Channels;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace AppSupervisor.Mqtt;

/// <summary>
/// Uses a short-lived MQTT 3.1.1 session for one connection test or publish transaction.
/// State subscriptions are established before publishing so fast device updates are not missed.
/// </summary>
internal sealed class MqttBrokerClient : IMqttBrokerClient
{
    private readonly MqttIntegrationConfig _integration;
    private readonly Func<IMqttTransport> _transportFactory;

    internal MqttBrokerClient(MqttIntegrationConfig integration)
        : this(integration, () => new MqttNetTransport(integration))
    {
    }

    internal MqttBrokerClient(
        MqttIntegrationConfig integration,
        Func<IMqttTransport> transportFactory)
    {
        _integration = integration;
        _transportFactory = transportFactory;
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        using IMqttTransport transport = _transportFactory();

        try
        {
            await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw CreateSafeException("MQTT broker connection failed", exception);
        }
        finally
        {
            await DisconnectQuietlyAsync(transport).ConfigureAwait(false);
        }
    }

    public async Task PublishAsync(
        MqttPublishMessage message,
        MqttStateCheck? verification,
        MqttRetainedStateCapture? capture,
        Action<byte[]>? stateCaptured,
        Action? publishAccepted,
        CancellationToken cancellationToken)
    {
        using IMqttTransport transport = _transportFactory();
        var messages = Channel.CreateUnbounded<MqttReceivedMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        transport.MessageReceived += received => messages.Writer.TryWrite(received);

        try
        {
            await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            string? stateTopic = capture?.Topic ?? verification?.Topic;

            if (stateTopic is not null)
                await transport.SubscribeAsync(stateTopic, cancellationToken).ConfigureAwait(false);

            if (capture is not null)
            {
                MqttReceivedMessage retained = await ReadMessageAsync(
                    messages.Reader,
                    capture.Topic,
                    expectedPayload: null,
                    requireRetained: true,
                    minimumSequence: 0,
                    capture.Timeout,
                    cancellationToken
                ).ConfigureAwait(false);
                stateCaptured?.Invoke([.. retained.Payload]);
            }

            long verificationSequence = transport.LastReceivedSequence;
            await transport.PublishAsync(message, cancellationToken).ConfigureAwait(false);
            publishAccepted?.Invoke();

            if (verification is not null)
            {
                await ReadMessageAsync(
                    messages.Reader,
                    verification.Topic,
                    verification.ExpectedPayload,
                    requireRetained: false,
                    minimumSequence: verificationSequence + 1,
                    verification.Timeout,
                    cancellationToken
                ).ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw CreateSafeException($"MQTT publish to '{message.Topic}' failed", exception);
        }
        finally
        {
            messages.Writer.TryComplete();
            await DisconnectQuietlyAsync(transport).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
    }

    private async Task<MqttReceivedMessage> ReadMessageAsync(
        ChannelReader<MqttReceivedMessage> messages,
        string topic,
        byte[]? expectedPayload,
        bool requireRetained,
        long minimumSequence,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        timeoutCancellation.CancelAfter(timeout);

        try
        {
            await foreach (MqttReceivedMessage message in messages.ReadAllAsync(
                timeoutCancellation.Token))
            {
                // A retained subscription replay can arrive after SUBACK. MQTT forwards a
                // new live state update to an existing subscriber with RETAIN cleared, so
                // verification must never accept a replay even when its bytes already match.
                if (message.Sequence < minimumSequence ||
                    !string.Equals(message.Topic, topic, StringComparison.Ordinal) ||
                    (requireRetained && !message.Retain) ||
                    (!requireRetained && message.Retain) ||
                    (expectedPayload is not null &&
                        !message.Payload.AsSpan().SequenceEqual(expectedPayload)))
                {
                    continue;
                }

                return message;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            string purpose = requireRetained ? "a retained state" : "the expected state";
            throw new TimeoutException(
                $"MQTT topic '{topic}' did not provide {purpose} within " +
                $"{timeout.TotalSeconds:0.###} seconds."
            );
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private InvalidOperationException CreateSafeException(string context, Exception exception)
    {
        string detail = exception.Message;

        if (!string.IsNullOrEmpty(_integration.Password))
        {
            detail = detail.Replace(
                _integration.Password,
                "[redacted]",
                StringComparison.Ordinal
            );
        }

        return new InvalidOperationException(
            $"{context} at '{_integration.Host}:{_integration.Port}'. {detail}",
            exception
        );
    }

    private static async Task DisconnectQuietlyAsync(IMqttTransport transport)
    {
        if (!transport.IsConnected)
            return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        try
        {
            await transport.DisconnectAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // The publish/connect outcome is authoritative; disposal still closes the socket.
        }
    }
}

/// <summary>Abstracts one MQTT network session for deterministic client orchestration tests.</summary>
internal interface IMqttTransport : IDisposable
{
    event Action<MqttReceivedMessage>? MessageReceived;
    bool IsConnected { get; }
    long LastReceivedSequence { get; }
    Task ConnectAsync(CancellationToken cancellationToken);
    Task SubscribeAsync(string topic, CancellationToken cancellationToken);
    Task PublishAsync(MqttPublishMessage message, CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
}

/// <summary>Contains one received broker message with a session-local sequence.</summary>
internal sealed record MqttReceivedMessage(
    long Sequence,
    string Topic,
    byte[] Payload,
    bool Retain
);

/// <summary>Maps the narrow transport contract to MQTTnet without relaxing TLS validation.</summary>
internal sealed class MqttNetTransport : IMqttTransport
{
    private readonly global::MQTTnet.IMqttClient _client;
    private readonly MqttClientOptions _options;
    private byte[]? _passwordBytes;
    private long _receivedSequence;

    internal MqttNetTransport(MqttIntegrationConfig integration)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithClientId($"AppSupervisor-{Guid.NewGuid():N}")
            .WithTcpServer(integration.Host, integration.Port)
            .WithProtocolVersion(MqttProtocolVersion.V311);

        if (!string.IsNullOrWhiteSpace(integration.Username))
        {
            _passwordBytes = System.Text.Encoding.UTF8.GetBytes(integration.Password ?? "");
            builder.WithCredentials(integration.Username, _passwordBytes);
        }

        if (integration.UseTls)
            builder.WithTlsOptions(new MqttClientTlsOptions { UseTls = true });

        _options = builder.Build();
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += args =>
        {
            long sequence = Interlocked.Increment(ref _receivedSequence);
            MessageReceived?.Invoke(new MqttReceivedMessage(
                sequence,
                args.ApplicationMessage.Topic,
                args.ApplicationMessage.Payload.ToArray(),
                args.ApplicationMessage.Retain
            ));
            return Task.CompletedTask;
        };
    }

    public event Action<MqttReceivedMessage>? MessageReceived;

    public bool IsConnected => _client.IsConnected;

    public long LastReceivedSequence => Volatile.Read(ref _receivedSequence);

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        timeoutCancellation.CancelAfter(TimeSpan.FromSeconds(10));
        MqttClientConnectResult result;

        try
        {
            result = await _client.ConnectAsync(
                _options,
                timeoutCancellation.Token
            ).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The broker connection timed out after 10 seconds.");
        }

        if (result.ResultCode != MqttClientConnectResultCode.Success)
            throw new InvalidOperationException($"Broker rejected the connection: {result.ResultCode}.");
    }

    public async Task SubscribeAsync(string topic, CancellationToken cancellationToken)
    {
        var factory = new MqttClientFactory();
        MqttClientSubscribeResult result = await _client.SubscribeAsync(
            factory.CreateSubscribeOptionsBuilder()
                .WithTopicFilter(filter => filter
                    .WithTopic(topic)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                .Build(),
            cancellationToken
        ).ConfigureAwait(false);

        if (result.Items.Any(item => item.ResultCode >= MqttClientSubscribeResultCode.UnspecifiedError))
            throw new InvalidOperationException("Broker rejected the state-topic subscription.");
    }

    public async Task PublishAsync(
        MqttPublishMessage message,
        CancellationToken cancellationToken)
    {
        var applicationMessage = new MqttApplicationMessageBuilder()
            .WithTopic(message.Topic)
            .WithPayload(message.Payload)
            .WithQualityOfServiceLevel(MapQos(message.Qos))
            .WithRetainFlag(message.Retain)
            .Build();
        MqttClientPublishResult result = await _client.PublishAsync(
            applicationMessage,
            cancellationToken
        ).ConfigureAwait(false);

        if (!result.IsSuccess)
            throw new InvalidOperationException($"Broker rejected the publish: {result.ReasonCode}.");
    }

    public Task DisconnectAsync(CancellationToken cancellationToken) =>
        _client.DisconnectAsync(
            new MqttClientDisconnectOptionsBuilder().Build(),
            cancellationToken
        );

    public void Dispose()
    {
        _client.Dispose();

        if (_passwordBytes is not null)
        {
            CryptographicOperations.ZeroMemory(_passwordBytes);
            _passwordBytes = null;
        }

        MessageReceived = null;
    }

    private static MqttQualityOfServiceLevel MapQos(MqttQualityOfService qos) => qos switch
    {
        MqttQualityOfService.AtMostOnce => MqttQualityOfServiceLevel.AtMostOnce,
        MqttQualityOfService.AtLeastOnce => MqttQualityOfServiceLevel.AtLeastOnce,
        MqttQualityOfService.ExactlyOnce => MqttQualityOfServiceLevel.ExactlyOnce,
        _ => throw new ArgumentOutOfRangeException(nameof(qos))
    };
}
