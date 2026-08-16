namespace AppSupervisor.Configuration;

/// <summary>Validates Windows audio endpoint actions.</summary>
public static partial class ConfigValidator
{
    private static void ValidateAudioInterfaces(
        SupervisorProfileConfig profile,
        string profileLabel,
        ICollection<string> errors)
    {
        if (profile.AudioInterfaces is null)
        {
            errors.Add($"{profileLabel} must contain an audioInterfaces array.");
            return;
        }

        for (int index = 0; index < profile.AudioInterfaces.Count; index++)
        {
            AudioInterfaceResourceConfig? resource = profile.AudioInterfaces[index];
            string label = $"{profileLabel}, audio interface entry {index + 1}";

            if (resource is null)
            {
                errors.Add($"{label} cannot be null.");
                continue;
            }

            ValidateNotifications(resource.Notifications, label, errors);

            if (!Enum.IsDefined(resource.Direction))
                errors.Add($"{label} has an unsupported direction.");

            if (resource.VolumePercent is < 0 or > 100)
                errors.Add($"{label} volumePercent must be between 0 and 100.");

            if (!resource.Enabled)
                continue;

            if (!resource.UseDefaultDevice &&
                string.IsNullOrWhiteSpace(resource.EndpointId) &&
                string.IsNullOrWhiteSpace(resource.DeviceInstanceId) &&
                string.IsNullOrWhiteSpace(resource.ContainerId))
            {
                errors.Add($"{label} must contain a selected Windows audio endpoint identity.");
            }

            if (!resource.UseDefaultDevice && string.IsNullOrWhiteSpace(resource.FriendlyName))
                errors.Add($"{label} must have a non-empty friendlyName.");
        }
    }
}
