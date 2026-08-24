namespace AppSupervisor.Configuration;

/// <summary>Validates deterministic Stream Deck profile actions.</summary>
public static partial class ConfigValidator
{
    private static void ValidateStreamDeckResources(
        SupervisorProfileConfig profile,
        string profileLabel,
        ICollection<string> errors)
    {
        if (profile.StreamDeckResources is null)
        {
            errors.Add($"{profileLabel} must contain a streamDeckResources array.");
            return;
        }

        for (int index = 0; index < profile.StreamDeckResources.Count; index++)
        {
            StreamDeckResourceConfig? resource = profile.StreamDeckResources[index];
            string label = $"{profileLabel}, Stream Deck entry {index + 1}";

            if (resource is null)
            {
                errors.Add($"{label} cannot be null.");
                continue;
            }

            ValidateNotifications(resource.Notifications, label, errors);

            if (!resource.Enabled)
                continue;

            if (string.IsNullOrWhiteSpace(resource.ActionId))
                errors.Add($"{label} must have a selected action.");

            if (resource.RestoreSwitchOnDeactivate && !resource.IsSwitch)
            {
                errors.Add(
                    $"{label} can restore on deactivation only when the selected action is a switch."
                );
            }
        }
    }
}
