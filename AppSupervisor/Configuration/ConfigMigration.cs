namespace AppSupervisor.Configuration;

/// <summary>Applies compatibility migrations before strict semantic validation.</summary>
internal static class ConfigMigration
{
    /// <summary>Converts legacy per-resource waits into explicit delay resources.</summary>
    public static void MigrateLegacyStartupWaits(IReadOnlyList<SupervisorProfileConfig?> profiles)
    {
        foreach (SupervisorProfileConfig? profile in profiles)
        {
            if (profile is null)
                continue;

            if (profile.Delays is null)
                continue;

            List<ManagedResourceConfig> ordered = EnumerateResources(profile)
                .Select((resource, stableOrder) => (resource, stableOrder))
                .OrderBy(item => item.resource.StartupOrder < 0
                    ? int.MaxValue
                    : item.resource.StartupOrder)
                .ThenBy(item => item.stableOrder)
                .Select(item => item.resource)
                .ToList();
            var migrated = new List<ManagedResourceConfig>();
            bool changed = false;

            foreach (ManagedResourceConfig resource in ordered)
            {
                migrated.Add(resource);
#pragma warning disable CS0618
                int legacyWait = resource.WaitAfterStartupMilliseconds;
                resource.WaitAfterStartupMilliseconds = 0;
#pragma warning restore CS0618

                if (legacyWait <= 0)
                    continue;

                changed = true;
                var delay = new DelayResourceConfig
                {
                    DurationMilliseconds = legacyWait
                };
                profile.Delays.Add(delay);
                migrated.Add(delay);
            }

            if (changed)
            {
                for (int index = 0; index < migrated.Count; index++)
                    migrated[index].StartupOrder = index;
            }
        }
    }

    private static IEnumerable<ManagedResourceConfig> EnumerateResources(
        SupervisorProfileConfig profile)
    {
        if (profile.Applications is not null)
            foreach (ManagedApplicationConfig? resource in profile.Applications)
                if (resource is not null)
                    yield return resource;

        if (profile.Services is not null)
            foreach (ManagedServiceConfig? resource in profile.Services)
                if (resource is not null)
                    yield return resource;

        if (profile.Delays is not null)
            foreach (DelayResourceConfig? resource in profile.Delays)
                if (resource is not null)
                    yield return resource;

        if (profile.HomeAssistantResources is not null)
            foreach (HomeAssistantResourceConfig? resource in profile.HomeAssistantResources)
                if (resource is not null)
                    yield return resource;
    }
}
