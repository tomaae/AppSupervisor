namespace AppSupervisor.HomeAssistant;

/// <summary>Contains Home Assistant services and entities available to the configuration editor.</summary>
public sealed record HomeAssistantCatalog(
    string Version,
    IReadOnlyList<HomeAssistantServiceInfo> Services,
    IReadOnlyList<HomeAssistantEntityInfo> Entities);

/// <summary>Describes one deterministic service supported by AppSupervisor.</summary>
public sealed record HomeAssistantServiceInfo(
    string Service,
    IReadOnlyList<string> EntityDomains)
{
    /// <summary>Gets the service identifier used in editor lists.</summary>
    public string DisplayName => Service;
}

/// <summary>Describes one Home Assistant entity and its current state.</summary>
public sealed record HomeAssistantEntityInfo(
    string EntityId,
    string FriendlyName,
    string State)
{
    /// <summary>Gets a searchable entity label that preserves the stable identifier.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(FriendlyName)
        ? $"{EntityId} [{State}]"
        : $"{FriendlyName} — {EntityId} [{State}]";
}
