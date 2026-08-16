namespace AppSupervisor.Configuration;

/// <summary>Validates delay and Home Assistant profile resources.</summary>
public static partial class ConfigValidator
{
    private static void ValidateDelayResources(
        SupervisorProfileConfig profile,
        string profileLabel,
        ICollection<string> errors)
    {
        if (profile.Delays is null)
        {
            errors.Add($"{profileLabel} must contain a delays array.");
            return;
        }

        for (int index = 0; index < profile.Delays.Count; index++)
        {
            DelayResourceConfig? delay = profile.Delays[index];
            string label = $"{profileLabel}, delay entry {index + 1}";

            if (delay is null)
            {
                errors.Add($"{label} cannot be null.");
                continue;
            }

            if (delay.DurationMilliseconds is < 0 or
                > ConfigurationLimits.MaximumWaitAfterStartupMilliseconds)
            {
                errors.Add(
                    $"{label} durationMilliseconds must be between 0 and " +
                    $"{ConfigurationLimits.MaximumWaitAfterStartupMilliseconds}."
                );
            }
        }
    }

    private static void ValidateHomeAssistantResources(
        SupervisorProfileConfig profile,
        string profileLabel,
        ICollection<string> errors,
        IDictionary<string, string> activeEntities,
        bool profileEnabled)
    {
        if (profile.HomeAssistantResources is null)
        {
            errors.Add($"{profileLabel} must contain a homeAssistantResources array.");
            return;
        }

        for (int index = 0; index < profile.HomeAssistantResources.Count; index++)
        {
            HomeAssistantResourceConfig? resource = profile.HomeAssistantResources[index];
            string label = $"{profileLabel}, Home Assistant entry {index + 1}";

            if (resource is null)
            {
                errors.Add($"{label} cannot be null.");
                continue;
            }

            ValidateNotifications(resource.Notifications, label, errors);

            if (!resource.Enabled)
                continue;

            string[] serviceParts = (resource.Service ?? "").Split('.', 2);
            string[] entityParts = (resource.EntityId ?? "").Split('.', 2);

            if (serviceParts.Length != 2 || serviceParts.Any(string.IsNullOrWhiteSpace))
            {
                errors.Add($"{label} must have a service in domain.service form.");
                continue;
            }

            if (entityParts.Length != 2 || entityParts.Any(string.IsNullOrWhiteSpace))
            {
                errors.Add($"{label} must have an entityId in domain.entity form.");
                continue;
            }

            if (!string.Equals(serviceParts[0], entityParts[0], StringComparison.OrdinalIgnoreCase))
                errors.Add($"{label} service and entity must use the same Home Assistant domain.");

            bool stateful = serviceParts[1] is "turn_on" or "turn_off";
            bool buttonPress = serviceParts[0] == "button" && serviceParts[1] == "press";

            if (!stateful && !buttonPress)
            {
                errors.Add(
                    $"{label} service must be a deterministic turn_on, turn_off, or button.press action."
                );
            }

            if (buttonPress && (resource.VerifyStateChange || resource.Persistent))
            {
                errors.Add(
                    $"{label} cannot verify or persist button.press because Home Assistant buttons are stateless."
                );
            }

            if (!profileEnabled)
                continue;

            string entityId = resource.EntityId!.Trim();

            if (activeEntities.TryGetValue(entityId, out string? existingOwner))
            {
                errors.Add(
                    $"{label} duplicates the Home Assistant entity already used by " +
                    $"{existingOwner}: {entityId}"
                );
            }
            else
            {
                activeEntities.Add(entityId, label);
            }
        }
    }
}
