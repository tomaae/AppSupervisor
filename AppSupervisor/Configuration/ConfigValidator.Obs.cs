namespace AppSupervisor.Configuration;

/// <summary>Validates deterministic OBS profile actions.</summary>
public static partial class ConfigValidator
{
    private static void ValidateObsResources(
        SupervisorProfileConfig profile,
        string profileLabel,
        ICollection<string> errors)
    {
        if (profile.ObsResources is null)
        {
            errors.Add($"{profileLabel} must contain an obsResources array.");
            return;
        }

        for (int index = 0; index < profile.ObsResources.Count; index++)
        {
            ObsResourceConfig? resource = profile.ObsResources[index];
            string label = $"{profileLabel}, OBS entry {index + 1}";

            if (resource is null)
            {
                errors.Add($"{label} cannot be null.");
                continue;
            }

            ValidateNotifications(resource.Notifications, label, errors);

            if (!Enum.IsDefined(resource.Action))
            {
                errors.Add($"{label} has an unsupported action.");
                continue;
            }

            if (!resource.Enabled)
                continue;

            switch (resource.Action)
            {
                case ObsActionType.SwitchScene:
                    Require(resource.SceneName, "sceneName", label, errors);
                    break;
                case ObsActionType.SetInputMute:
                    Require(resource.SceneName, "sceneName", label, errors);
                    Require(resource.InputName, "inputName", label, errors);
                    break;
                case ObsActionType.SetSourceVisibility:
                    Require(resource.SceneName, "sceneName", label, errors);
                    Require(resource.SourceName, "sourceName", label, errors);
                    break;
            }
        }
    }

    private static void Require(
        string? value,
        string propertyName,
        string label,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"{label} must have a non-empty {propertyName}.");
    }
}
