namespace AppSupervisor.Configuration;

public static partial class ConfigValidator
{
    private static readonly HashSet<int> CommercialLengths = [30, 60, 90, 120, 150, 180];

    private static void ValidateTwitchResources(
        SupervisorProfileConfig profile,
        string profileLabel,
        ICollection<string> errors,
        ref string? activeTwitchProfile)
    {
        if (profile.TwitchResources is null)
        {
            errors.Add($"{profileLabel} must contain a twitchResources array.");
            return;
        }

        var activeModes = new HashSet<TwitchActionType>();
        bool hasEnabled = false;
        for (int index = 0; index < profile.TwitchResources.Count; index++)
        {
            TwitchResourceConfig? resource = profile.TwitchResources[index];
            string label = $"{profileLabel}, Twitch entry {index + 1}";
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
            if (!profile.Enabled || !resource.Enabled)
                continue;

            hasEnabled = true;
            switch (resource.Action)
            {
                case TwitchActionType.SendChatMessage when resource.Message.Length is < 1 or > 500:
                    errors.Add($"{label} message must contain between 1 and 500 characters.");
                    break;
                case TwitchActionType.RunCommercial when !CommercialLengths.Contains(resource.CommercialLengthSeconds):
                    errors.Add($"{label} commercialLengthSeconds must be 30, 60, 90, 120, 150, or 180.");
                    break;
                case TwitchActionType.FollowersOnly when resource.FollowerDurationMinutes is < 0 or > 129_600:
                    errors.Add($"{label} followerDurationMinutes must be between 0 and 129600.");
                    break;
                case TwitchActionType.SlowMode when resource.SlowModeWaitSeconds is < 3 or > 120:
                    errors.Add($"{label} slowModeWaitSeconds must be between 3 and 120.");
                    break;
            }

            if (resource.Action is TwitchActionType.EmoteOnly or TwitchActionType.FollowersOnly or
                TwitchActionType.SlowMode or TwitchActionType.SubscribersOnly)
            {
                if (!activeModes.Add(resource.Action))
                    errors.Add($"{profileLabel} contains more than one enabled {resource.Action} Twitch action.");
            }
        }

        if (!hasEnabled)
            return;
        if (activeTwitchProfile is null)
            activeTwitchProfile = profileLabel;
        else
            errors.Add($"{profileLabel} uses Twitch resources, but Twitch is already used by {activeTwitchProfile}. Only one enabled profile may control Twitch.");
    }
}
