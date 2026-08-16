namespace AppSupervisor.WindowsAudio;

/// <summary>Resolves a configured endpoint after Windows or a driver changes its endpoint ID.</summary>
internal static class WindowsAudioEndpointResolver
{
    public static AudioEndpointSnapshot Resolve(
        AudioInterfaceResourceConfig configuration,
        IReadOnlyList<AudioEndpointSnapshot> endpoints)
    {
        AudioEndpointSnapshot[] candidates = endpoints
            .Where(endpoint =>
                endpoint.Direction == configuration.Direction && !endpoint.FollowsDefault)
            .ToArray();

        AudioEndpointSnapshot? exact = Unique(candidates, endpoint => Same(
            endpoint.EndpointId,
            configuration.EndpointId
        ));
        if (exact is not null)
            return exact;

        exact = Unique(candidates, endpoint => SameNonEmpty(
            endpoint.DeviceInstanceId,
            configuration.DeviceInstanceId
        ));
        if (exact is not null)
            return exact;

        AudioEndpointSnapshot[] sameContainer = candidates
            .Where(endpoint => SameNonEmpty(endpoint.ContainerId, configuration.ContainerId))
            .ToArray();
        if (sameContainer.Length == 1)
            return sameContainer[0];
        if (sameContainer.Length > 1)
        {
            exact = Unique(sameContainer, endpoint => NamesMatch(endpoint, configuration));
            if (exact is not null)
                return exact;

            throw Ambiguous(configuration);
        }

        exact = Unique(candidates, endpoint => NamesMatch(endpoint, configuration));
        if (exact is not null)
            return exact;

        if (candidates.Any(endpoint => NamesMatch(endpoint, configuration)))
            throw Ambiguous(configuration);

        throw new InvalidOperationException(
            $"Windows audio {(configuration.Direction == AudioInterfaceDirection.Output ? "output" : "input")} " +
            $"'{DisplayName(configuration)}' is not currently available."
        );
    }

    private static AudioEndpointSnapshot? Unique(
        IEnumerable<AudioEndpointSnapshot> endpoints,
        Func<AudioEndpointSnapshot, bool> predicate)
    {
        AudioEndpointSnapshot[] matches = endpoints.Where(predicate).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool NamesMatch(
        AudioEndpointSnapshot endpoint,
        AudioInterfaceResourceConfig configuration)
    {
        bool friendlyMatches = SameNonEmpty(endpoint.FriendlyName, configuration.FriendlyName);
        bool interfaceMatches = SameNonEmpty(endpoint.InterfaceName, configuration.InterfaceName);

        if (!string.IsNullOrWhiteSpace(configuration.FriendlyName) &&
            !string.IsNullOrWhiteSpace(configuration.InterfaceName))
        {
            return friendlyMatches && interfaceMatches;
        }

        return friendlyMatches || interfaceMatches;
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SameNonEmpty(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) && Same(left, right);

    private static InvalidOperationException Ambiguous(AudioInterfaceResourceConfig configuration) =>
        new(
            $"More than one active Windows audio endpoint matches '{DisplayName(configuration)}'. " +
            "Open the configuration editor and select the interface again."
        );

    private static string DisplayName(AudioInterfaceResourceConfig configuration) =>
        string.IsNullOrWhiteSpace(configuration.FriendlyName)
            ? configuration.EndpointId
            : configuration.FriendlyName;
}
