namespace AppSupervisor.Configuration;

/// <summary>
/// Validates cross-type resource startup order, nonblocking waits, stable identifiers, and dependencies.
/// </summary>
public static partial class ConfigValidator
{
    /// <summary>Validates one profile's combined resource startup sequence.</summary>
    /// <param name="profile">The enabled profile whose resources are being validated.</param>
    /// <param name="profileLabel">The user-readable profile identifier used in errors.</param>
    /// <param name="errors">The collection that receives validation errors.</param>
    private static void ValidateResourceStartup(
        SupervisorProfileConfig profile,
        string profileLabel,
        ICollection<string> errors)
    {
        if (profile.Applications is null || profile.Services is null ||
            profile.Delays is null || profile.HomeAssistantResources is null ||
            profile.MqttResources is null ||
            profile.ObsResources is null || profile.StreamDeckResources is null ||
            profile.TwitchResources is null ||
            profile.AudioInterfaces is null)
            return;

        List<(ManagedResourceConfig Config, string Label)> resources = [];
        resources.AddRange(profile.Applications
            .Select((application, index) => (
                (ManagedResourceConfig?)application,
                $"{profileLabel}, application entry {index + 1}"
            ))
            .Where(item => item.Item1 is not null)
            .Select(item => (item.Item1!, item.Item2)));
        resources.AddRange(profile.Services
            .Select((service, index) => (
                (ManagedResourceConfig?)service,
                $"{profileLabel}, service entry {index + 1}"
            ))
            .Where(item => item.Item1 is not null)
            .Select(item => (item.Item1!, item.Item2)));
        resources.AddRange(profile.Delays
            .Select((delay, index) => (
                (ManagedResourceConfig?)delay,
                $"{profileLabel}, delay entry {index + 1}"
            ))
            .Where(item => item.Item1 is not null)
            .Select(item => (item.Item1!, item.Item2)));
        resources.AddRange(profile.HomeAssistantResources
            .Select((resource, index) => (
                (ManagedResourceConfig?)resource,
                $"{profileLabel}, Home Assistant entry {index + 1}"
            ))
            .Where(item => item.Item1 is not null)
            .Select(item => (item.Item1!, item.Item2)));
        resources.AddRange(profile.MqttResources
            .Select((resource, index) => (
                (ManagedResourceConfig?)resource,
                $"{profileLabel}, MQTT entry {index + 1}"
            ))
            .Where(item => item.Item1 is not null)
            .Select(item => (item.Item1!, item.Item2)));
        resources.AddRange(profile.ObsResources
            .Select((resource, index) => (
                (ManagedResourceConfig?)resource,
                $"{profileLabel}, OBS entry {index + 1}"
            ))
            .Where(item => item.Item1 is not null)
            .Select(item => (item.Item1!, item.Item2)));
        resources.AddRange(profile.StreamDeckResources
            .Select((resource, index) => (
                (ManagedResourceConfig?)resource,
                $"{profileLabel}, Stream Deck entry {index + 1}"
            ))
            .Where(item => item.Item1 is not null)
            .Select(item => (item.Item1!, item.Item2)));
        resources.AddRange(profile.TwitchResources
            .Select((resource, index) => (
                (ManagedResourceConfig?)resource,
                $"{profileLabel}, Twitch entry {index + 1}"
            ))
            .Where(item => item.Item1 is not null)
            .Select(item => (item.Item1!, item.Item2)));
        resources.AddRange(profile.AudioInterfaces
            .Select((resource, index) => (
                (ManagedResourceConfig?)resource,
                $"{profileLabel}, audio interface entry {index + 1}"
            ))
            .Where(item => item.Item1 is not null)
            .Select(item => (item.Item1!, item.Item2)));

        var resourcesById = new Dictionary<string, (ManagedResourceConfig Config, string Label)>(
            StringComparer.OrdinalIgnoreCase
        );
        var resourcesByOrder = new Dictionary<int, string>();

        foreach ((ManagedResourceConfig resource, string resourceLabel) in resources)
        {
            string resourceId = resource.ResourceId?.Trim() ?? "";

            if (resourceId.Length == 0)
            {
                errors.Add($"{resourceLabel} must have a non-empty resourceId.");
            }
            else if (!resourcesById.TryAdd(resourceId, (resource, resourceLabel)))
            {
                errors.Add($"{resourceLabel} duplicates resourceId '{resourceId}'.");
            }

            if (resource.StartupOrder < -1)
            {
                errors.Add($"{resourceLabel} startupOrder must be -1 or zero or greater.");
            }
            else if (resource.StartupOrder >= 0 &&
                !resourcesByOrder.TryAdd(resource.StartupOrder, resourceLabel))
            {
                errors.Add(
                    $"{resourceLabel} duplicates startupOrder {resource.StartupOrder} already used by " +
                    $"{resourcesByOrder[resource.StartupOrder]}."
                );
            }

#pragma warning disable CS0618
            if (resource.WaitAfterStartupMilliseconds != 0)
            {
                errors.Add(
                    $"{resourceLabel} uses obsolete waitAfterStartupMilliseconds; " +
                    "use a separate delay resource instead."
                );
            }
#pragma warning restore CS0618

        }

        foreach ((ManagedResourceConfig resource, string resourceLabel) in resources)
        {
            string dependencyId = resource.DependencyResourceId?.Trim() ?? "";

            if (dependencyId.Length == 0)
                continue;

            if (!resourcesById.TryGetValue(
                dependencyId,
                out (ManagedResourceConfig Config, string Label) dependency))
            {
                errors.Add($"{resourceLabel} references missing dependencyResourceId '{dependencyId}'.");
                continue;
            }

            if (resource.StartupOrder < 0 || dependency.Config.StartupOrder < 0)
            {
                errors.Add(
                    $"{resourceLabel} and its dependency must both have an explicit startup order."
                );
            }
            else if (dependency.Config.StartupOrder >= resource.StartupOrder)
            {
                errors.Add($"{resourceLabel} dependency must appear earlier in startup order.");
            }

            if (resource.Enabled && !dependency.Config.Enabled)
                errors.Add($"{resourceLabel} cannot depend on a disabled resource.");
        }
    }
}
