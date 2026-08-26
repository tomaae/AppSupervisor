using AppSupervisor.Notifications;

namespace AppSupervisor.Configuration;

/// <summary>
/// Validates configuration semantics that JSON deserialization alone cannot enforce.
/// </summary>
public static partial class ConfigValidator
{
    /// <summary>
    /// Validates profile identity, trigger names, timeout values, helper paths, service names, and per-helper notification targets.
    /// </summary>
    /// <param name="profiles">The deserialized profile entries, including possible JSON null entries.</param>
    /// <exception cref="ConfigValidationException">Thrown when one or more entries are invalid.</exception>
    public static void Validate(IReadOnlyList<SupervisorProfileConfig?> profiles) =>
        Validate(profiles, requireApplicationFiles: true);

    /// <summary>
    /// Validates one portable profile as though it were enabled without requiring its source-computer
    /// executables to exist on the importing computer.
    /// </summary>
    internal static void ValidatePortableProfile(SupervisorProfileConfig profile)
    {
        SupervisorProfileConfig validationCopy = ConfigJson.Clone(profile);
        validationCopy.Enabled = true;
        Validate([validationCopy], requireApplicationFiles: false);
    }

    private static void Validate(
        IReadOnlyList<SupervisorProfileConfig?> profiles,
        bool requireApplicationFiles)
    {
        var errors = new List<string>();
        var profileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeServiceNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var activeHomeAssistantEntities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? activeTwitchProfile = null;

        for (int profileIndex = 0; profileIndex < profiles.Count; profileIndex++)
        {
            var profile = profiles[profileIndex];

            if (profile is null)
            {
                errors.Add($"Profile entry {profileIndex + 1} cannot be null.");
                continue;
            }

            string profileLabel = string.IsNullOrWhiteSpace(profile.Name)
                ? $"Profile entry {profileIndex + 1}"
                : $"Profile '{profile.Name}'";

            if (string.IsNullOrWhiteSpace(profile.ProfileId))
            {
                errors.Add($"{profileLabel} must have a non-empty profileId.");
            }
            else if (!profileIds.Add(profile.ProfileId.Trim()))
            {
                errors.Add($"Profile profileId '{profile.ProfileId}' is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                errors.Add($"{profileLabel} must have a non-empty name.");
            }
            else if (!profileNames.Add(profile.Name.Trim()))
            {
                errors.Add($"Profile name '{profile.Name}' is duplicated.");
            }

            ValidateMonitorProcess(profile, profileLabel, errors);
            ValidateTimeoutValue(profile.CloseTimeoutSeconds, "closeTimeoutSeconds", profileLabel, errors);
            ValidateTimeoutValue(profile.RestartTimeoutSeconds, "restartTimeoutSeconds", profileLabel, errors);

            if (profile.Applications is null)
            {
                errors.Add($"{profileLabel} must contain an applications array.");
            }
            else
            {
                ValidateApplications(
                    profile,
                    profileLabel,
                    errors,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    profile.Enabled,
                    requireApplicationFiles
                );
            }

            if (profile.Services is null)
            {
                errors.Add($"{profileLabel} must contain a services array.");
            }
            else
            {
                ValidateServices(profile, profileLabel, errors, activeServiceNames, profile.Enabled);
            }

            ValidateDelayResources(profile, profileLabel, errors);
            ValidateHomeAssistantResources(
                profile,
                profileLabel,
                errors,
                activeHomeAssistantEntities,
                profile.Enabled
            );
            ValidateMqttResources(profile, profileLabel, errors);
            ValidateObsResources(profile, profileLabel, errors);
            ValidateStreamDeckResources(profile, profileLabel, errors);
            ValidateTwitchResources(profile, profileLabel, errors, ref activeTwitchProfile);
            ValidateAudioInterfaces(profile, profileLabel, errors);

            ValidateResourceStartup(profile, profileLabel, errors);
        }

        if (errors.Count > 0)
            throw new ConfigValidationException(errors);
    }

    /// <summary>
    /// Validates that a profile contains a usable process name for its activation trigger.
    /// </summary>
    /// <param name="profile">The profile being validated.</param>
    /// <param name="profileLabel">The user-readable profile identifier used in errors.</param>
    /// <param name="errors">The collection that receives validation errors.</param>
    private static void ValidateMonitorProcess(
        SupervisorProfileConfig profile,
        string profileLabel,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(profile.MonitorProcess))
        {
            errors.Add($"{profileLabel} must have a non-empty monitorProcess.");
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(profile.MonitorProcess)))
                errors.Add($"{profileLabel} has an invalid monitorProcess value.");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            errors.Add($"{profileLabel} has an invalid monitorProcess value: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates that an optional timeout value is not negative.
    /// </summary>
    /// <param name="value">The optional number of seconds.</param>
    /// <param name="propertyName">The JSON property name shown in errors.</param>
    /// <param name="profileLabel">The user-readable profile identifier used in errors.</param>
    /// <param name="errors">The collection that receives validation errors.</param>
    private static void ValidateTimeoutValue(
        int? value,
        string propertyName,
        string profileLabel,
        ICollection<string> errors)
    {
        if (value is < 0 or > ConfigurationLimits.MaximumTimeoutSeconds)
        {
            errors.Add(
                $"{profileLabel} has an invalid {propertyName}; the value must be between 0 and " +
                $"{ConfigurationLimits.MaximumTimeoutSeconds}."
            );
        }
    }

    /// <summary>
    /// Validates one helper's compact target array and rejects explicit nulls or duplicate destinations.
    /// </summary>
    /// <param name="notifications">The helper-specific notification configuration.</param>
    /// <param name="resourceLabel">The user-readable helper identifier used in errors.</param>
    /// <param name="errors">The collection that receives validation errors.</param>
    private static void ValidateNotifications(
        NotificationConfig? notifications,
        string resourceLabel,
        ICollection<string> errors)
    {
        if (notifications is null)
        {
            errors.Add($"{resourceLabel} must contain a notifications object.");
            return;
        }

        if (notifications.Target is null)
        {
            errors.Add($"{resourceLabel} notifications must contain a target array.");
            return;
        }

        var targets = new HashSet<NotificationTarget>();

        foreach (NotificationTarget target in notifications.Target)
        {
            if (!Enum.IsDefined(target))
            {
                errors.Add($"{resourceLabel} contains an unsupported notification target: {target}.");
            }
            else if (!targets.Add(target))
            {
                errors.Add($"{resourceLabel} contains duplicate notification target '{target}'.");
            }
        }
    }

    /// <summary>
    /// Validates helper entries, their notifications, and duplicate executable paths within one profile.
    /// </summary>
    /// <param name="profile">The enabled profile whose helper entries are being validated.</param>
    /// <param name="profileEnabled">Whether active executable checks apply to this profile.</param>
    /// <param name="profileLabel">The user-readable profile identifier used in errors.</param>
    /// <param name="errors">The collection that receives validation errors.</param>
    /// <param name="activeApplicationPaths">Canonical paths already used within this profile.</param>
    private static void ValidateApplications(
        SupervisorProfileConfig profile,
        string profileLabel,
        ICollection<string> errors,
        IDictionary<string, string> activeApplicationPaths,
        bool profileEnabled,
        bool requireApplicationFiles)
    {
        for (int applicationIndex = 0; applicationIndex < profile.Applications.Count; applicationIndex++)
        {
            var application = profile.Applications[applicationIndex];

            if (application is null)
            {
                errors.Add($"{profileLabel}, application entry {applicationIndex + 1} cannot be null.");
                continue;
            }

            string applicationLabel = $"{profileLabel}, application entry {applicationIndex + 1}";
            ValidateNotifications(application.Notifications, applicationLabel, errors);
            ValidateHealthChecks(application, applicationLabel, errors);
            ValidateStartupMacros(application, applicationLabel, errors);

            if (!profileEnabled || !application.Enabled)
                continue;

            if (string.IsNullOrWhiteSpace(application.Path))
            {
                errors.Add($"{applicationLabel} must have a non-empty path.");
                continue;
            }

            if (!Path.IsPathFullyQualified(application.Path))
            {
                errors.Add($"{applicationLabel} path must be fully qualified: {application.Path}");
                continue;
            }

            string fullPath;

            try
            {
                fullPath = Path.GetFullPath(application.Path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                errors.Add($"{applicationLabel} has an invalid path: {ex.Message}");
                continue;
            }

            if (requireApplicationFiles && !File.Exists(fullPath))
                errors.Add($"{applicationLabel} executable does not exist: {fullPath}");

            ValidateAppUri(application, applicationLabel, errors);

            if (activeApplicationPaths.TryGetValue(fullPath, out string? existingOwner))
            {
                errors.Add($"{applicationLabel} duplicates the helper path already used by {existingOwner}: {fullPath}");
            }
            else
            {
                activeApplicationPaths.Add(fullPath, applicationLabel);
            }
        }
    }

    /// <summary>
    /// Validates service entries, their notifications, and duplicate active service names.
    /// </summary>
    /// <param name="profileEnabled">Whether active service checks apply to this profile.</param>
    /// <param name="profile">The enabled profile whose service entries are being validated.</param>
    /// <param name="profileLabel">The user-readable profile identifier used in errors.</param>
    /// <param name="errors">The collection that receives validation errors.</param>
    /// <param name="activeServiceNames">Service names already owned by active configuration entries.</param>
    private static void ValidateServices(
        SupervisorProfileConfig profile,
        string profileLabel,
        ICollection<string> errors,
        IDictionary<string, string> activeServiceNames,
        bool profileEnabled)
    {
        for (int serviceIndex = 0; serviceIndex < profile.Services.Count; serviceIndex++)
        {
            var service = profile.Services[serviceIndex];

            if (service is null)
            {
                errors.Add($"{profileLabel}, service entry {serviceIndex + 1} cannot be null.");
                continue;
            }

            string serviceLabel = $"{profileLabel}, service entry {serviceIndex + 1}";
            ValidateNotifications(service.Notifications, serviceLabel, errors);

            if (!profileEnabled || !service.Enabled)
                continue;

            if (string.IsNullOrWhiteSpace(service.ServiceName))
            {
                errors.Add($"{serviceLabel} must have a non-empty serviceName.");
                continue;
            }

            string normalizedName = service.ServiceName.Trim();

            if (activeServiceNames.TryGetValue(normalizedName, out string? existingOwner))
            {
                errors.Add(
                    $"{serviceLabel} duplicates the Windows service already used by {existingOwner}: {normalizedName}"
                );
            }
            else
            {
                activeServiceNames.Add(normalizedName, serviceLabel);
            }
        }
    }
}
