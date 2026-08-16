using System.Windows.Forms;

namespace AppSupervisor.Configuration;

public static partial class ConfigValidator
{
    private const int MaximumStartupMacroDelayMilliseconds = 86_400_000;
    private const int MaximumWindowDimension = 65_535;

    /// <summary>Validates an application's ordered startup macro actions.</summary>
    private static void ValidateStartupMacros(
        ManagedApplicationConfig application,
        string applicationLabel,
        ICollection<string> errors)
    {
        if (application.StartupMacros is null)
        {
            errors.Add($"{applicationLabel} must contain a startupMacros array.");
            return;
        }

        for (int index = 0; index < application.StartupMacros.Count; index++)
        {
            StartupMacroActionConfig? action = application.StartupMacros[index];
            string label = $"{applicationLabel}, startup macro action {index + 1}";

            if (action is null)
            {
                errors.Add($"{label} cannot be null.");
                continue;
            }

            if (action.Type is not StartupMacroActionType type || !Enum.IsDefined(type))
            {
                errors.Add($"{label} must have a supported type.");
                continue;
            }

            switch (type)
            {
                case StartupMacroActionType.Delay:
                    if (action.DelayMilliseconds is < 0 or > MaximumStartupMacroDelayMilliseconds)
                    {
                        errors.Add(
                            $"{label} delayMilliseconds must be between 0 and {MaximumStartupMacroDelayMilliseconds}."
                        );
                    }
                    break;

                case StartupMacroActionType.Hotkey:
                    ValidateHotkey(action, label, errors);
                    break;

                case StartupMacroActionType.MoveWindow:
                    if (action.X is null || action.Y is null)
                        errors.Add($"{label} must specify both x and y coordinates.");
                    break;

                case StartupMacroActionType.ResizeWindow:
                    if (action.Width is null or <= 0 or > MaximumWindowDimension ||
                        action.Height is null or <= 0 or > MaximumWindowDimension)
                    {
                        errors.Add(
                            $"{label} width and height must be between 1 and {MaximumWindowDimension}."
                        );
                    }
                    break;
            }
        }
    }

    private static void ValidateHotkey(
        StartupMacroActionConfig action,
        string label,
        ICollection<string> errors)
    {
        if (action.Keys is null || action.Keys.Count == 0)
        {
            errors.Add($"{label} must contain at least one captured key.");
            return;
        }

        if (action.Keys.Count > 16)
        {
            errors.Add($"{label} cannot contain more than 16 keys.");
            return;
        }

        var parsed = new HashSet<Keys>();
        bool hasNonModifier = false;

        foreach (string keyName in action.Keys)
        {
            if (!Enum.TryParse(keyName, ignoreCase: true, out Keys key) ||
                key is Keys.None || !parsed.Add(key))
            {
                errors.Add($"{label} contains an invalid or duplicate key '{keyName}'.");
                continue;
            }

            hasNonModifier |= !StartupMacroWindowActions.IsModifierKey(key);
        }

        if (!hasNonModifier)
            errors.Add($"{label} must contain at least one non-modifier key.");
    }
}
