namespace AppSupervisor.HomeAssistant;

/// <summary>Provides deterministic state and inverse-action semantics for supported Home Assistant services.</summary>
internal static class HomeAssistantServiceSemantics
{
    private const int DefaultBrightnessPercent = 100;

    /// <summary>Returns the entity state expected after a supported stateful service call.</summary>
    /// <param name="service">The Home Assistant service in domain.action form.</param>
    /// <returns><c>on</c>, <c>off</c>, or <see langword="null"/> for a stateless or unsupported action.</returns>
    public static string? GetDesiredState(string service)
    {
        string[] parts = service.Split('.', 2);

        if (parts.Length != 2)
            return null;

        return parts[1] switch
        {
            "turn_on" => "on",
            "turn_off" => "off",
            _ => null
        };
    }

    /// <summary>Returns the deterministic inverse of a supported stateful service.</summary>
    /// <param name="service">The Home Assistant service in domain.action form.</param>
    /// <returns>The inverse service, or <see langword="null"/> for a stateless or unsupported action.</returns>
    public static string? GetReverseService(string service)
    {
        string[] parts = service.Split('.', 2);

        if (parts.Length != 2)
            return null;

        return parts[1] switch
        {
            "turn_on" => $"{parts[0]}.turn_off",
            "turn_off" => $"{parts[0]}.turn_on",
            _ => null
        };
    }

    /// <summary>Returns whether a service accepts AppSupervisor's brightness-percentage option.</summary>
    /// <param name="service">The Home Assistant service in domain.action form.</param>
    /// <returns>True only for light.turn_on.</returns>
    public static bool SupportsBrightnessPercentage(string service) =>
        string.Equals(service, "light.turn_on", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the effective brightness for a supported service.</summary>
    /// <param name="service">The Home Assistant service in domain.action form.</param>
    /// <param name="configuredBrightnessPercent">The explicitly configured percentage.</param>
    /// <returns>The configured percentage, 100 for an older light.turn_on entry, or null.</returns>
    public static int? GetBrightnessPercentage(
        string service,
        int? configuredBrightnessPercent)
    {
        return SupportsBrightnessPercentage(service)
            ? configuredBrightnessPercent ?? DefaultBrightnessPercent
            : null;
    }
}
