using System.Text;
using AppSupervisor.Mqtt;

namespace AppSupervisor.Tests;

/// <summary>Verifies safe editor previews for every MQTT deactivation mode.</summary>
public sealed class MqttActionTesterTests
{
    [Fact]
    public async Task RunAsync_OneShotPublishesOnceWithoutDelayOrInverse()
    {
        var client = new FakeMqttClient();
        bool delayed = false;

        MqttActionTestResult result = await MqttActionTester.RunAsync(
            client,
            Configuration(MqttDeactivationBehavior.OneShot),
            (_, _) =>
            {
                delayed = true;
                return Task.CompletedTask;
            },
            CancellationToken.None
        );

        Assert.False(delayed);
        Assert.False(result.InversePublished);
        Assert.Single(client.Messages);
    }

    [Fact]
    public async Task RunAsync_ConfiguredInverseWaitsFiveSecondsThenPublishesReverse()
    {
        var client = new FakeMqttClient();
        TimeSpan? delay = null;

        MqttActionTestResult result = await MqttActionTester.RunAsync(
            client,
            Configuration(MqttDeactivationBehavior.PublishConfiguredPayload),
            (duration, _) =>
            {
                delay = duration;
                return Task.CompletedTask;
            },
            CancellationToken.None
        );

        Assert.Equal(TimeSpan.FromSeconds(5), delay);
        Assert.True(result.InversePublished);
        Assert.Equal(["ON", "OFF"], client.Messages.Select(message => Text(message.Payload)));
    }

    [Fact]
    public async Task RunAsync_RetainedRestoreUsesExactCapturedBytes()
    {
        var client = new FakeMqttClient { CapturedState = [0, 0xFF, 0x80] };

        await MqttActionTester.RunAsync(
            client,
            Configuration(MqttDeactivationBehavior.RestoreRetainedState),
            (_, _) => Task.CompletedTask,
            CancellationToken.None
        );

        Assert.Equal([0, 0xFF, 0x80], client.Messages[1].Payload);
        Assert.Equal([0, 0xFF, 0x80], client.Verifications[1]!.ExpectedPayload);
    }

    [Fact]
    public async Task RunAsync_ActivationVerificationFailureAfterAcceptStillAppliesInverse()
    {
        var client = new FakeMqttClient { FailFirstAfterAccept = true };

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MqttActionTester.RunAsync(
                client,
                Configuration(MqttDeactivationBehavior.PublishConfiguredPayload),
                (_, _) => Task.CompletedTask,
                CancellationToken.None
            )
        );

        Assert.Contains("inverse was published", exception.Message);
        Assert.Equal(["ON", "OFF"], client.Messages.Select(message => Text(message.Payload)));
    }

    [Fact]
    public async Task RunAsync_FailureBeforePublishDoesNotInventInverse()
    {
        var client = new FakeMqttClient { FailFirstBeforeAccept = true };

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MqttActionTester.RunAsync(
                client,
                Configuration(MqttDeactivationBehavior.PublishConfiguredPayload),
                (_, _) => Task.CompletedTask,
                CancellationToken.None
            )
        );

        Assert.Contains("activation publish was not accepted", exception.Message);
        Assert.Empty(client.Messages);
    }

    private static MqttResourceConfig Configuration(MqttDeactivationBehavior behavior) => new()
    {
        Topic = "device/set",
        Payload = "ON",
        VerificationTopic = "device/state",
        VerificationTimeoutSeconds = 1,
        DeactivationBehavior = behavior,
        DeactivationTopic = "device/set",
        DeactivationPayload = "OFF",
        DeactivationRetain = true
    };

    private static string Text(byte[] value) => Encoding.UTF8.GetString(value);

    private sealed class FakeMqttClient : IMqttBrokerClient
    {
        private int _calls;
        public List<MqttPublishMessage> Messages { get; } = [];
        public List<MqttStateCheck?> Verifications { get; } = [];
        public byte[]? CapturedState { get; set; }
        public bool FailFirstBeforeAccept { get; set; }
        public bool FailFirstAfterAccept { get; set; }

        public Task TestConnectionAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PublishAsync(
            MqttPublishMessage message,
            MqttStateCheck? verification,
            MqttRetainedStateCapture? capture,
            Action<byte[]>? stateCaptured,
            Action? publishAccepted,
            CancellationToken cancellationToken)
        {
            _calls++;

            if (_calls == 1 && capture is not null && CapturedState is not null)
                stateCaptured?.Invoke([.. CapturedState]);

            if (_calls == 1 && FailFirstBeforeAccept)
                throw new InvalidOperationException("Connection failed.");

            Messages.Add(message with { Payload = [.. message.Payload] });
            Verifications.Add(verification);
            publishAccepted?.Invoke();

            if (_calls == 1 && FailFirstAfterAccept)
                throw new TimeoutException("State verification timed out.");

            return Task.CompletedTask;
        }

        public void Dispose() { }
    }
}
