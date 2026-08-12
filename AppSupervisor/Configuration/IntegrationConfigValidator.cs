using AppSupervisor.Notifications;

namespace AppSupervisor.Configuration;

/// <summary>Validates application-wide integration configuration.</summary>
public static class IntegrationConfigValidator
{
    /// <summary>Validates global SteamVR settings and stable expected-device identities.</summary>
    public static void Validate(IntegrationsConfig integrations)
    {
        var errors = new List<string>();
        SteamVrIntegrationConfig? steamVr = integrations.SteamVr;

        if (steamVr is null)
        {
            errors.Add("The integrations object must contain a steamVr object.");
        }
        else
        {
            if (steamVr.ReminderIntervalMinutes is < 1 or > 1_440)
                errors.Add("SteamVR reminderIntervalMinutes must be between 1 and 1440.");

            if (steamVr.Devices is null)
            {
                errors.Add("SteamVR integration must contain a devices array.");
            }
            else
            {
                var serials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int index = 0; index < steamVr.Devices.Count; index++)
                {
                    SteamVrDeviceConfig? device = steamVr.Devices[index];
                    string label = $"SteamVR device entry {index + 1}";

                    if (device is null)
                    {
                        errors.Add($"{label} cannot be null.");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(device.SerialNumber))
                        errors.Add($"{label} must have a non-empty serialNumber.");
                    else if (!serials.Add(device.SerialNumber))
                        errors.Add($"SteamVR serialNumber '{device.SerialNumber}' is duplicated.");

                    if (string.IsNullOrWhiteSpace(device.Name))
                        errors.Add($"{label} must have a non-empty name.");

                    if (!Enum.IsDefined(device.DeviceClass))
                        errors.Add($"{label} has an unsupported deviceClass.");
                }
            }

            ValidateNotifications(steamVr.Notifications, errors);
        }

        if (errors.Count > 0)
            throw new ConfigValidationException(errors);
    }

    private static void ValidateNotifications(
        NotificationConfig? notifications,
        ICollection<string> errors)
    {
        if (notifications?.Target is null)
        {
            errors.Add("SteamVR integration notifications must contain a target array.");
            return;
        }

        var targets = new HashSet<NotificationTarget>();

        foreach (NotificationTarget target in notifications.Target)
        {
            if (!Enum.IsDefined(target))
                errors.Add($"SteamVR integration contains unsupported notification target: {target}.");
            else if (!targets.Add(target))
                errors.Add($"SteamVR integration contains duplicate notification target '{target}'.");
        }
    }
}
