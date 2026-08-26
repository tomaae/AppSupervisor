using System.Text;
using AppSupervisor.Mqtt;
using AppSupervisor.Resources;

namespace AppSupervisor.Tests;

/// <summary>Verifies one-shot and reversible MQTT resource lifecycle behavior.</summary>
public sealed class MqttResourceTests
{
    [Fact]
    public void OneShot_DeactivationDoesNotPublishAgain()
    {
        var client = new FakeMqttClient();
        using var resource = CreateResource(client, MqttDeactivationBehavior.OneShot);

        ActivateAndDrain(resource);
        resource.Deactivate();
        resource.AdvanceLifecycle(DateTime.UtcNow);

        Assert.Single(client.Messages);
        Assert.False(resource.DeactivationPending);
    }

    [Fact]
    public void ConfiguredInverse_DeactivationPublishesExplicitMessageSettings()
    {
        var client = new FakeMqttClient();
        using var resource = CreateResource(
            client,
            MqttDeactivationBehavior.PublishConfiguredPayload
        );

        ActivateAndDrain(resource);
        resource.Deactivate();
        Drain(resource);

        Assert.Collection(
            client.Messages,
            activation =>
            {
                Assert.Equal("device/set", activation.Topic);
                Assert.Equal("ON", Text(activation.Payload));
            },
            inverse =>
            {
                Assert.Equal("device/reverse", inverse.Topic);
                Assert.Equal("OFF", Text(inverse.Payload));
                Assert.Equal(MqttQualityOfService.ExactlyOnce, inverse.Qos);
                Assert.True(inverse.Retain);
            }
        );
    }

    [Fact]
    public void RetainedRestore_RestoresExactCapturedBytesAndAlwaysVerifies()
    {
        byte[] original = [0, 0xFF, 0x41, 0x80];
        var client = new FakeMqttClient { CapturedState = original };
        using var resource = CreateResource(
            client,
            MqttDeactivationBehavior.RestoreRetainedState
        );

        ActivateAndDrain(resource);
        original[0] = 99;
        resource.Deactivate();
        Drain(resource);

        Assert.Equal(2, client.Messages.Count);
        Assert.Equal([0, 0xFF, 0x41, 0x80], client.Messages[1].Payload);
        MqttStateCheck inverseCheck = Assert.IsType<MqttStateCheck>(client.Verifications[1]);
        Assert.Equal("device/state", inverseCheck.Topic);
        Assert.Equal([0, 0xFF, 0x41, 0x80], inverseCheck.ExpectedPayload);
        Assert.True(client.Messages[1].Retain);
    }

    [Fact]
    public void FailedBeforePublishAccepted_DoesNotSendConfiguredInverse()
    {
        var client = new FakeMqttClient { FailBeforePublishAccepted = true };
        using var resource = CreateResource(
            client,
            MqttDeactivationBehavior.PublishConfiguredPayload
        );
        resource.Activate();
        resource.Deactivate();

        resource.AdvanceLifecycle(DateTime.UtcNow);
        Assert.True(SpinWait.SpinUntil(
            () => !resource.LifecycleWorkPending,
            TimeSpan.FromSeconds(2)
        ));
        resource.AdvanceLifecycle(DateTime.UtcNow.AddHours(1));

        Assert.Empty(client.Messages);
    }

    [Fact]
    public void DeactivateDuringAcceptedActivation_QueuesInverseAfterActivationDrains()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeMqttClient { ReleasePublish = release.Task };
        using var resource = CreateResource(
            client,
            MqttDeactivationBehavior.PublishConfiguredPayload
        );
        resource.Activate();
        resource.AdvanceLifecycle(DateTime.UtcNow);
        Assert.True(SpinWait.SpinUntil(() => client.AcceptedCount == 1, TimeSpan.FromSeconds(2)));

        resource.Deactivate();
        release.SetResult();
        Assert.True(SpinWait.SpinUntil(
            () => resource.DeactivationPending,
            TimeSpan.FromSeconds(2)
        ));
        Drain(resource);

        Assert.Equal(["ON", "OFF"], client.Messages.Select(item => Text(item.Payload)));
    }

    private static MqttResource CreateResource(
        FakeMqttClient client,
        MqttDeactivationBehavior behavior)
    {
        return new MqttResource(new MqttResourceConfig
        {
            Topic = "device/set",
            Payload = "ON",
            Qos = MqttQualityOfService.AtLeastOnce,
            VerificationTopic = "device/state",
            VerificationTimeoutSeconds = 1,
            DeactivationBehavior = behavior,
            DeactivationTopic = "device/reverse",
            DeactivationPayload = "OFF",
            DeactivationQos = MqttQualityOfService.ExactlyOnce,
            DeactivationRetain = true
        }, client);
    }

    private static void ActivateAndDrain(MqttResource resource)
    {
        resource.Activate();
        Drain(resource);
        Assert.True(resource.IsStarted());
    }

    private static void Drain(MqttResource resource)
    {
        Assert.True(SpinWait.SpinUntil(() =>
        {
            resource.AdvanceLifecycle(DateTime.UtcNow.AddHours(1));
            return !resource.LifecycleWorkPending;
        }, TimeSpan.FromSeconds(2)));
    }

    private static string Text(byte[] value) => Encoding.UTF8.GetString(value);

    private sealed class FakeMqttClient : IMqttBrokerClient
    {
        public List<MqttPublishMessage> Messages { get; } = [];
        public List<MqttStateCheck?> Verifications { get; } = [];
        public byte[]? CapturedState { get; set; }
        public bool FailBeforePublishAccepted { get; set; }
        public Task ReleasePublish { get; set; } = Task.CompletedTask;
        public int AcceptedCount { get; private set; }

        public Task TestConnectionAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public async Task PublishAsync(
            MqttPublishMessage message,
            MqttStateCheck? verification,
            MqttRetainedStateCapture? capture,
            Action<byte[]>? stateCaptured,
            Action? publishAccepted,
            CancellationToken cancellationToken)
        {
            if (capture is not null && CapturedState is not null)
                stateCaptured?.Invoke([.. CapturedState]);

            if (FailBeforePublishAccepted)
                throw new InvalidOperationException("No publish occurred.");

            Messages.Add(message with { Payload = [.. message.Payload] });
            Verifications.Add(verification);
            AcceptedCount++;
            publishAccepted?.Invoke();
            await ReleasePublish.WaitAsync(cancellationToken);
        }

        public void Dispose() { }
    }
}
