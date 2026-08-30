using AppSupervisor.Notifications;
using AppSupervisor.Bluetooth;

namespace AppSupervisor.Configuration;

/// <summary>Validates application-wide integration configuration.</summary>
public static class IntegrationConfigValidator
{
    /// <summary>Validates global integration settings and their enabled profile consumers.</summary>
    public static void Validate(
        IntegrationsConfig integrations,
        IReadOnlyList<SupervisorProfileConfig?>? profiles = null)
    {
        var errors = new List<string>();
        if (!Enum.IsDefined(integrations.LogLevel))
            errors.Add("The integrations logLevel is unsupported.");
        if (integrations.SupervisorApi is null)
            errors.Add("The integrations object must contain a supervisorApi object.");
        ValidateHomeAssistant(integrations.HomeAssistant, profiles, errors);
        ValidateMqtt(integrations.Mqtt, profiles, errors);
        ValidateTwitch(integrations.Twitch, errors);
        ValidateObs(integrations.Obs, profiles, errors);
        ValidateBluetooth(integrations.Bluetooth, profiles, errors);
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

                    if (!Enum.IsDefined(device.Role))
                        errors.Add($"{label} has an unsupported role.");
                }
            }

            ValidateNotifications(steamVr.Notifications, errors);
        }

        if (errors.Count > 0)
            throw new ConfigValidationException(errors);
    }

    private static void ValidateObs(
        ObsIntegrationConfig? obs,
        IReadOnlyList<SupervisorProfileConfig?>? profiles,
        ICollection<string> errors)
    {
        if (obs is null)
        {
            errors.Add("The integrations object must contain an obs object.");
            return;
        }

        bool required = profiles?.Any(profile => profile?.Enabled == true &&
            profile.ObsResources?.Any(resource => resource?.Enabled == true) == true) == true;
        bool configured = !string.IsNullOrWhiteSpace(obs.Host) || !string.IsNullOrEmpty(obs.Password) ||
            obs.Port != 4455;

        if (!required && !configured)
            return;

        if (string.IsNullOrWhiteSpace(obs.Host))
            errors.Add("OBS WebSocket host must be provided when the integration is configured or used.");

        if (obs.Port is < 1 or > 65_535)
            errors.Add("OBS WebSocket port must be between 1 and 65535.");
    }

    private static void ValidateBluetooth(
        BluetoothIntegrationConfig? bluetooth,
        IReadOnlyList<SupervisorProfileConfig?>? profiles,
        ICollection<string> errors)
    {
        if (bluetooth is null)
        {
            errors.Add("The integrations object must contain a bluetooth object.");
            return;
        }

        if (bluetooth.ScanIntervalSeconds is < 10 or > 300)
            errors.Add("Bluetooth scanIntervalSeconds must be between 10 and 300.");
        if (bluetooth.PresenceTimeoutSeconds is < 10 or > 900)
            errors.Add("Bluetooth presenceTimeoutSeconds must be between 10 and 900.");
        else if (bluetooth.PresenceTimeoutSeconds < bluetooth.ScanIntervalSeconds)
        {
            errors.Add(
                "Bluetooth presenceTimeoutSeconds must be at least scanIntervalSeconds."
            );
        }

        if (bluetooth.Devices is null)
        {
            errors.Add("Bluetooth integration must contain a devices array.");
            return;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < bluetooth.Devices.Count; index++)
        {
            BluetoothDeviceConfig? device = bluetooth.Devices[index];
            string label = $"Bluetooth device entry {index + 1}";

            if (device is null)
            {
                errors.Add($"{label} cannot be null.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(device.DeviceId))
                errors.Add($"{label} must have a non-empty deviceId.");
            else if (!ids.Add(device.DeviceId.Trim()))
                errors.Add($"Bluetooth deviceId '{device.DeviceId}' is duplicated.");

            if (string.IsNullOrWhiteSpace(device.Name))
                errors.Add($"{label} must have a non-empty name.");

            if (!Enum.IsDefined(device.Kind))
                errors.Add($"{label} has an unsupported kind.");

            string address = BluetoothDeviceScanner.NormalizeAddress(device.Address);
            if (address.Length == 0)
            {
                errors.Add(
                    $"{label} address must contain exactly twelve hexadecimal digits."
                );
            }
            else if (!addresses.Add($"{device.Kind}:{address}"))
            {
                errors.Add(
                    $"Bluetooth {device.Kind} address '{address}' is duplicated."
                );
            }
        }

        if (profiles is null)
            return;

        foreach (SupervisorProfileConfig? profile in profiles)
        {
            if (profile is null || profile.TriggerType != ProfileTriggerType.BluetoothDevice)
                continue;

            foreach (string? selectedDeviceId in profile.MonitorBluetoothDeviceIds ?? [])
            {
                string deviceId = selectedDeviceId?.Trim() ?? "";
                if (deviceId.Length == 0 || ids.Contains(deviceId))
                    continue;

                string profileName = string.IsNullOrWhiteSpace(profile.Name)
                    ? "An unnamed profile"
                    : $"Profile '{profile.Name}'";
                errors.Add(
                    $"{profileName} references Bluetooth deviceId '{deviceId}', which is not registered globally."
                );
            }
        }
    }

    private static void ValidateHomeAssistant(
        HomeAssistantIntegrationConfig? homeAssistant,
        IReadOnlyList<SupervisorProfileConfig?>? profiles,
        ICollection<string> errors)
    {
        if (homeAssistant is null)
        {
            errors.Add("The integrations object must contain a homeAssistant object.");
            return;
        }

        bool hasUrl = !string.IsNullOrWhiteSpace(homeAssistant.Url);
        bool hasToken = !string.IsNullOrWhiteSpace(homeAssistant.Token);
        bool required = profiles?.Any(profile => profile?.Enabled == true &&
            profile.HomeAssistantResources?.Any(resource => resource?.Enabled == true) == true) == true;

        if (!hasUrl && !hasToken && !required)
            return;

        if (!hasUrl)
            errors.Add("Home Assistant URL must be provided when the integration is configured or used.");
        else if (!Uri.TryCreate(homeAssistant.Url, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
        {
            errors.Add("Home Assistant URL must be an absolute HTTP or HTTPS URL.");
        }

        if (!hasToken)
            errors.Add("Home Assistant token must be provided when the integration is configured or used.");
    }

    private static void ValidateMqtt(
        MqttIntegrationConfig? mqtt,
        IReadOnlyList<SupervisorProfileConfig?>? profiles,
        ICollection<string> errors)
    {
        if (mqtt is null)
        {
            errors.Add("The integrations object must contain an mqtt object.");
            return;
        }

        bool required = profiles?.Any(profile => profile?.Enabled == true &&
            profile.MqttResources?.Any(resource => resource?.Enabled == true) == true) == true;
        bool configured = !string.IsNullOrWhiteSpace(mqtt.Host) || mqtt.Port != 1883 ||
            mqtt.UseTls || !string.IsNullOrEmpty(mqtt.Username) ||
            !string.IsNullOrEmpty(mqtt.Password);

        if (!required && !configured)
            return;

        if (string.IsNullOrWhiteSpace(mqtt.Host))
        {
            errors.Add("MQTT broker host must be provided when the integration is configured or used.");
        }
        else if (mqtt.Host.Contains("://", StringComparison.Ordinal) ||
            mqtt.Host.IndexOfAny(['/', '\\']) >= 0)
        {
            errors.Add("MQTT broker host must be a DNS name or IP address without a URI scheme or path.");
        }

        if (mqtt.Port is < 1 or > 65_535)
            errors.Add("MQTT broker port must be between 1 and 65535.");

        if (!string.IsNullOrEmpty(mqtt.Password) && string.IsNullOrWhiteSpace(mqtt.Username))
            errors.Add("MQTT username must be provided when a password is configured.");
    }

    private static void ValidateTwitch(
        TwitchIntegrationConfig? twitch,
        ICollection<string> errors)
    {
        if (twitch is null)
        {
            errors.Add("The integrations object must contain a twitch object.");
            return;
        }
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
