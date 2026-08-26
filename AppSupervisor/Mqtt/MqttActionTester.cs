using System.Text;

namespace AppSupervisor.Mqtt;

/// <summary>Runs one editor preview and applies the configured deterministic inverse when available.</summary>
internal static class MqttActionTester
{
    private static readonly TimeSpan PreviewDuration = TimeSpan.FromSeconds(5);

    internal static Task<MqttActionTestResult> RunAsync(
        IMqttBrokerClient client,
        MqttResourceConfig configuration,
        CancellationToken cancellationToken) =>
        RunAsync(
            client,
            configuration,
            (delay, token) => Task.Delay(delay, token),
            cancellationToken
        );

    internal static async Task<MqttActionTestResult> RunAsync(
        IMqttBrokerClient client,
        MqttResourceConfig configuration,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(delay);

        TimeSpan timeout = TimeSpan.FromSeconds(configuration.VerificationTimeoutSeconds);
        byte[]? capturedState = null;
        bool publishAccepted = false;
        bool inversePublished = false;
        Exception? activationFailure = null;

        try
        {
            await client.PublishAsync(
                new MqttPublishMessage(
                    configuration.Topic,
                    Encoding.UTF8.GetBytes(configuration.Payload),
                    configuration.Qos,
                    configuration.Retain
                ),
                configuration.VerifyStateChange
                    ? new MqttStateCheck(
                        configuration.VerificationTopic,
                        Encoding.UTF8.GetBytes(configuration.ExpectedState),
                        timeout
                    )
                    : null,
                configuration.DeactivationBehavior ==
                    MqttDeactivationBehavior.RestoreRetainedState
                        ? new MqttRetainedStateCapture(
                            configuration.VerificationTopic,
                            timeout
                        )
                        : null,
                payload => capturedState = [.. payload],
                () => publishAccepted = true,
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            activationFailure = exception;
        }

        bool reversible = configuration.DeactivationBehavior switch
        {
            MqttDeactivationBehavior.PublishConfiguredPayload => true,
            MqttDeactivationBehavior.RestoreRetainedState => capturedState is not null,
            _ => false
        };

        if (activationFailure is null && reversible)
            await delay(PreviewDuration, CancellationToken.None).ConfigureAwait(false);

        if (reversible && publishAccepted)
        {
            byte[] reversePayload = configuration.DeactivationBehavior ==
                MqttDeactivationBehavior.RestoreRetainedState
                    ? capturedState!
                    : Encoding.UTF8.GetBytes(configuration.DeactivationPayload);
            MqttStateCheck? verification = configuration.DeactivationBehavior ==
                MqttDeactivationBehavior.RestoreRetainedState
                    ? new MqttStateCheck(configuration.VerificationTopic, reversePayload, timeout)
                    : configuration.VerifyDeactivation
                        ? new MqttStateCheck(
                            configuration.VerificationTopic,
                            Encoding.UTF8.GetBytes(configuration.DeactivationExpectedState),
                            timeout
                        )
                        : null;

            try
            {
                await client.PublishAsync(
                    new MqttPublishMessage(
                        configuration.DeactivationTopic,
                        reversePayload,
                        configuration.DeactivationQos,
                        configuration.DeactivationRetain
                    ),
                    verification,
                    capture: null,
                    stateCaptured: null,
                    publishAccepted: null,
                    CancellationToken.None
                ).ConfigureAwait(false);
                inversePublished = true;
            }
            catch (Exception reverseFailure)
            {
                throw new InvalidOperationException(
                    "The MQTT preview could not apply its configured inverse. " +
                    "Restore the target manually. " + reverseFailure.Message,
                    reverseFailure
                );
            }
        }

        if (activationFailure is not null)
        {
            string cleanup = inversePublished
                ? " The configured inverse was published afterward."
                : !publishAccepted
                    ? " No inverse was sent because the activation publish was not accepted."
                    : " No deterministic inverse was available.";
            throw new InvalidOperationException(
                "The MQTT preview activation failed." + cleanup + " " +
                activationFailure.Message,
                activationFailure
            );
        }

        return new MqttActionTestResult(
            configuration.DeactivationBehavior,
            InversePublished: inversePublished
        );
    }
}

/// <summary>Describes whether an MQTT editor preview applied an inverse.</summary>
internal sealed record MqttActionTestResult(
    MqttDeactivationBehavior Behavior,
    bool InversePublished
);
