namespace AppSupervisor.Configuration;

/// <summary>Validates cross-profile activation prerequisites.</summary>
public static partial class ConfigValidator
{
    private static void ValidateProfileDependencies(
        IReadOnlyList<SupervisorProfileConfig?> profiles,
        ICollection<string> errors)
    {
        var profilesById = profiles
            .Where(profile => profile is not null && !string.IsNullOrWhiteSpace(profile.ProfileId))
            .GroupBy(profile => profile!.ProfileId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single()!, StringComparer.OrdinalIgnoreCase);

        foreach (SupervisorProfileConfig profile in profiles.OfType<SupervisorProfileConfig>())
        {
            string dependencyId = profile.DependencyProfileId?.Trim() ?? "";

            if (dependencyId.Length == 0)
                continue;

            string profileLabel = string.IsNullOrWhiteSpace(profile.Name)
                ? "Profile"
                : $"Profile '{profile.Name}'";

            if (string.Equals(
                dependencyId,
                profile.ProfileId?.Trim(),
                StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{profileLabel} cannot depend on itself.");
                continue;
            }

            if (!profilesById.TryGetValue(dependencyId, out SupervisorProfileConfig? dependency))
            {
                errors.Add($"{profileLabel} references missing dependencyProfileId '{dependencyId}'.");
                continue;
            }

            if (profile.Enabled && !dependency.Enabled)
            {
                errors.Add(
                    $"{profileLabel} cannot depend on disabled profile '{dependency.Name}'."
                );
            }
        }

        DetectProfileDependencyCycles(profilesById, errors);
    }

    private static void DetectProfileDependencyCycles(
        IReadOnlyDictionary<string, SupervisorProfileConfig> profilesById,
        ICollection<string> errors)
    {
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string startId in profilesById.Keys)
        {
            if (completed.Contains(startId))
                continue;

            var path = new List<string>();
            var pathIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string? currentId = startId;

            while (currentId is not null &&
                profilesById.TryGetValue(
                    currentId,
                    out SupervisorProfileConfig? current))
            {
                if (completed.Contains(currentId))
                    break;

                if (pathIndexes.TryGetValue(currentId, out int cycleStart))
                {
                    string[] cycleIds = [.. path.Skip(cycleStart)];
                    string cycleKey = string.Join(
                        "|",
                        cycleIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    );

                    if (cycleIds.Length > 1 && reported.Add(cycleKey))
                    {
                        string names = string.Join(
                            " -> ",
                            cycleIds.Select(id => profilesById[id].Name)
                                .Append(profilesById[cycleIds[0]].Name)
                        );
                        errors.Add($"Profile dependency cycle detected: {names}.");
                    }

                    break;
                }

                pathIndexes.Add(currentId, path.Count);
                path.Add(currentId);
                string dependencyId = current.DependencyProfileId?.Trim() ?? "";
                currentId = dependencyId.Length == 0 ? null : dependencyId;
            }

            foreach (string visitedId in path)
                completed.Add(visitedId);
        }
    }
}
