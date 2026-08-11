namespace AppSupervisor.Configuration;

/// <summary>
/// Adds per-application network health-check validation to the main configuration validator.
/// </summary>
public static partial class ConfigValidator
{
    /// <summary>Validates health-check identity, timing, type-specific fields, parameter freshness, and notification targets.</summary>
    /// <param name="application">The application whose checks are being validated.</param>
    /// <param name="applicationLabel">The user-readable application identifier used in errors.</param>
    /// <param name="errors">The collection that receives validation errors.</param>
    private static void ValidateHealthChecks(
        ManagedApplicationConfig application,
        string applicationLabel,
        ICollection<string> errors)
    {
        if (application.HealthChecks is null)
        {
            errors.Add($"{applicationLabel} must contain a healthChecks array.");
            return;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < application.HealthChecks.Count; index++)
        {
            HealthCheckConfig? healthCheck = application.HealthChecks[index];
            string checkLabel = $"{applicationLabel}, health check entry {index + 1}";

            if (healthCheck is null)
            {
                errors.Add($"{checkLabel} cannot be null.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(healthCheck.Name))
            {
                errors.Add($"{checkLabel} must have a non-empty name.");
            }
            else if (!names.Add(healthCheck.Name.Trim()))
            {
                errors.Add($"{applicationLabel} duplicates health-check name '{healthCheck.Name}'.");
            }

            ValidateNotifications(healthCheck.Notifications, checkLabel, errors);
            ValidatePositive(healthCheck.IntervalSeconds, "intervalSeconds", checkLabel, errors);
            ValidatePositive(healthCheck.TimeoutSeconds, "timeoutSeconds", checkLabel, errors);
            ValidatePositive(healthCheck.FailureThreshold, "failureThreshold", checkLabel, errors);

            ValidateMaximum(healthCheck.IntervalSeconds, ConfigurationLimits.MaximumHealthIntervalSeconds, "intervalSeconds", checkLabel, errors);
            ValidateMaximum(healthCheck.TimeoutSeconds, ConfigurationLimits.MaximumHealthProbeTimeoutSeconds, "timeoutSeconds", checkLabel, errors);
            ValidateMaximum(healthCheck.FailureThreshold, ConfigurationLimits.MaximumHealthFailureThreshold, "failureThreshold", checkLabel, errors);

            if (healthCheck.StartupDelaySeconds is < 0 or > ConfigurationLimits.MaximumHealthStartupDelaySeconds)
            {
                errors.Add(
                    $"{checkLabel} has an invalid startupDelaySeconds; the value must be between 0 and " +
                    $"{ConfigurationLimits.MaximumHealthStartupDelaySeconds}."
                );
            }

            if (healthCheck.Type is null)
            {
                errors.Add($"{checkLabel} must specify a type.");
                continue;
            }

            switch (healthCheck.Type.Value)
            {
                case HealthCheckType.Listener:
                    ValidateListenerHealthCheck(healthCheck, checkLabel, errors);
                    break;

                case HealthCheckType.Vrcosc:
                    ValidateVrcOscHealthCheck(healthCheck, checkLabel, errors);
                    break;
            }
        }
    }

    /// <summary>Validates one listener check's required endpoint fields and forbidden OSC fields.</summary>
    /// <param name="healthCheck">The listener configuration.</param>
    /// <param name="checkLabel">The user-readable check identifier.</param>
    /// <param name="errors">The collection that receives validation errors.</param>
    private static void ValidateListenerHealthCheck(
        HealthCheckConfig healthCheck,
        string checkLabel,
        ICollection<string> errors)
    {
        if (healthCheck.Protocol is null)
            errors.Add($"{checkLabel} listener must specify protocol.");

        if (healthCheck.Port is null or < 1 or > 65535)
            errors.Add($"{checkLabel} listener port must be between 1 and 65535.");

        if (healthCheck.Parameters is null)
        {
            errors.Add($"{checkLabel} must contain a parameters array.");
        }
        else if (healthCheck.Parameters.Count > 0)
        {
            errors.Add($"{checkLabel} listener cannot configure OSC parameters.");
        }

        if (healthCheck.StaleSeconds is not null)
            errors.Add($"{checkLabel} listener cannot configure staleSeconds.");

        ValidateOptionalProcessName(
            healthCheck.ActiveWhenProcess,
            "activeWhenProcess",
            checkLabel,
            errors
        );
    }

    /// <summary>Validates that vrcosc discovers its own endpoint and has a usable optional freshness parameter set.</summary>
    /// <param name="healthCheck">The vrcosc configuration.</param>
    /// <param name="checkLabel">The user-readable check identifier.</param>
    /// <param name="errors">The collection that receives validation errors.</param>
    private static void ValidateVrcOscHealthCheck(
        HealthCheckConfig healthCheck,
        string checkLabel,
        ICollection<string> errors)
    {
        if (healthCheck.Protocol is not null)
            errors.Add($"{checkLabel} vrcosc cannot configure protocol; OSCQuery discovers it.");

        if (healthCheck.Port is not null)
            errors.Add($"{checkLabel} vrcosc cannot configure port; OSCQuery discovers it.");

        if (!string.IsNullOrWhiteSpace(healthCheck.ActiveWhenProcess))
        {
            errors.Add(
                $"{checkLabel} vrcosc cannot configure activeWhenProcess; it is always bound to VRChat.exe."
            );
        }

        if (healthCheck.Parameters is null)
        {
            errors.Add($"{checkLabel} must contain a parameters array.");
            return;
        }

        var parameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string? parameter in healthCheck.Parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter))
            {
                errors.Add($"{checkLabel} contains an empty OSC parameter name.");
            }
            else if (!parameterNames.Add(parameter.Trim()))
            {
                errors.Add($"{checkLabel} duplicates OSC parameter '{parameter}'.");
            }
        }

        if (healthCheck.StaleSeconds is <= 0 or > ConfigurationLimits.MaximumHealthStaleSeconds)
        {
            errors.Add(
                $"{checkLabel} staleSeconds must be between 1 and {ConfigurationLimits.MaximumHealthStaleSeconds} when specified."
            );
        }

        if (healthCheck.StaleSeconds is not null && parameterNames.Count < 2)
        {
            errors.Add(
                $"{checkLabel} parameter freshness requires at least two distinct parameters."
            );
        }
    }

    /// <summary>Validates a strictly positive integer timing or threshold value.</summary>
    /// <param name="value">The configured value.</param>
    /// <param name="propertyName">The JSON property name shown in errors.</param>
    /// <param name="checkLabel">The user-readable check identifier.</param>
    /// <param name="errors">The collection that receives validation errors.</param>
    private static void ValidatePositive(
        int value,
        string propertyName,
        string checkLabel,
        ICollection<string> errors)
    {
        if (value <= 0)
            errors.Add($"{checkLabel} {propertyName} must be greater than zero.");
    }

    /// <summary>Validates the upper bound of a positive health-check setting.</summary>
    private static void ValidateMaximum(
        int value,
        int maximum,
        string propertyName,
        string checkLabel,
        ICollection<string> errors)
    {
        if (value > maximum)
            errors.Add($"{checkLabel} {propertyName} must be {maximum} or less.");
    }

    /// <summary>Validates an optional process-gating name without requiring a full path or fixed address.</summary>
    /// <param name="processName">The optional configured process name.</param>
    /// <param name="propertyName">The JSON property name shown in errors.</param>
    /// <param name="checkLabel">The user-readable check identifier.</param>
    /// <param name="errors">The collection that receives validation errors.</param>
    private static void ValidateOptionalProcessName(
        string processName,
        string propertyName,
        string checkLabel,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return;

        try
        {
            if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(processName)))
                errors.Add($"{checkLabel} has an invalid {propertyName} value.");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            errors.Add($"{checkLabel} has an invalid {propertyName} value: {ex.Message}");
        }
    }
}
