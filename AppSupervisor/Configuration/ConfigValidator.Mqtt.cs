using System.Text;

namespace AppSupervisor.Configuration;

/// <summary>Validates MQTT publish, verification, and deterministic inverse settings.</summary>
public static partial class ConfigValidator
{
    private const int MaximumMqttTopicBytes = 65_535;
    private const int MaximumMqttPayloadBytes = 1_048_576;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    private static void ValidateMqttResources(
        SupervisorProfileConfig profile,
        string profileLabel,
        ICollection<string> errors)
    {
        if (profile.MqttResources is null)
        {
            errors.Add($"{profileLabel} must contain an mqttResources array.");
            return;
        }

        for (int index = 0; index < profile.MqttResources.Count; index++)
        {
            MqttResourceConfig? resource = profile.MqttResources[index];
            string label = $"{profileLabel}, MQTT entry {index + 1}";

            if (resource is null)
            {
                errors.Add($"{label} cannot be null.");
                continue;
            }

            ValidateNotifications(resource.Notifications, label, errors);

            if (!resource.Enabled)
                continue;

            ValidateMqttTopic(resource.Topic, $"{label} topic", errors);
            ValidateMqttPayload(resource.Payload, $"{label} payload", errors);
            ValidateMqttQos(resource.Qos, $"{label} qos", errors);

            if (resource.VerificationTimeoutSeconds is < 1 or > 300)
            {
                errors.Add(
                    $"{label} verificationTimeoutSeconds must be between 1 and 300."
                );
            }

            if (resource.VerifyStateChange ||
                resource.DeactivationBehavior == MqttDeactivationBehavior.RestoreRetainedState ||
                resource.VerifyDeactivation)
            {
                ValidateMqttTopic(
                    resource.VerificationTopic,
                    $"{label} verificationTopic",
                    errors
                );
            }

            if (resource.VerifyStateChange)
            {
                ValidateMqttPayload(
                    resource.ExpectedState,
                    $"{label} expectedState",
                    errors
                );
            }

            if (!Enum.IsDefined(resource.DeactivationBehavior))
            {
                errors.Add($"{label} has an unsupported deactivationBehavior.");
                continue;
            }

            if (resource.DeactivationBehavior == MqttDeactivationBehavior.OneShot)
            {
                if (resource.VerifyDeactivation)
                    errors.Add($"{label} cannot verify deactivation for a one-shot publish.");
                continue;
            }

            ValidateMqttTopic(
                resource.DeactivationTopic,
                $"{label} deactivationTopic",
                errors
            );
            ValidateMqttQos(
                resource.DeactivationQos,
                $"{label} deactivationQos",
                errors
            );

            if (resource.DeactivationBehavior ==
                MqttDeactivationBehavior.PublishConfiguredPayload)
            {
                ValidateMqttPayload(
                    resource.DeactivationPayload,
                    $"{label} deactivationPayload",
                    errors
                );

                if (resource.VerifyDeactivation)
                {
                    ValidateMqttPayload(
                        resource.DeactivationExpectedState,
                        $"{label} deactivationExpectedState",
                        errors
                    );
                }
            }
            else
            {
                if (!resource.DeactivationRetain)
                {
                    errors.Add(
                        $"{label} deactivationRetain must be true when restoring retained state."
                    );
                }

                if (resource.VerifyDeactivation)
                {
                    errors.Add(
                        $"{label} restoreRetainedState always verifies restoration; " +
                        "verifyDeactivation must be false."
                    );
                }
            }
        }
    }

    private static void ValidateMqttTopic(
        string? value,
        string label,
        ICollection<string> errors)
    {
        if (string.IsNullOrEmpty(value))
        {
            errors.Add($"{label} must be non-empty.");
            return;
        }

        if (value.Contains('\0') || value.Contains('+') || value.Contains('#'))
        {
            errors.Add($"{label} must be an exact MQTT topic without nulls or wildcards.");
            return;
        }

        try
        {
            if (StrictUtf8.GetByteCount(value) > MaximumMqttTopicBytes)
                errors.Add($"{label} exceeds the MQTT 65535-byte UTF-8 limit.");
        }
        catch (EncoderFallbackException)
        {
            errors.Add($"{label} contains invalid Unicode text.");
        }
    }

    private static void ValidateMqttPayload(
        string? value,
        string label,
        ICollection<string> errors)
    {
        if (value is null)
        {
            errors.Add($"{label} cannot be null.");
            return;
        }

        try
        {
            if (StrictUtf8.GetByteCount(value) > MaximumMqttPayloadBytes)
                errors.Add($"{label} exceeds the 1 MB UTF-8 configuration limit.");
        }
        catch (EncoderFallbackException)
        {
            errors.Add($"{label} contains invalid Unicode text.");
        }
    }

    private static void ValidateMqttQos(
        MqttQualityOfService qos,
        string label,
        ICollection<string> errors)
    {
        if (!Enum.IsDefined(qos))
            errors.Add($"{label} is unsupported.");
    }
}
