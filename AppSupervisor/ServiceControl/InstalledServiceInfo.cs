namespace AppSupervisor.ServiceControl;

/// <summary>
/// Describes one installed third-party Windows service that can be selected in the configuration editor.
/// </summary>
internal sealed class InstalledServiceInfo
{
    /// <summary>
    /// Creates a service-catalog entry from Service Control Manager and executable metadata.
    /// </summary>
    /// <param name="serviceName">The internal Windows service name used by service-control APIs.</param>
    /// <param name="displayName">The friendly service name shown by Windows.</param>
    /// <param name="executablePath">The resolved service executable path, when available.</param>
    /// <param name="publisher">The executable publisher, when available.</param>
    /// <param name="isAutomaticStart">Whether Windows currently starts the service automatically.</param>
    /// <param name="isConfiguredOnly">Whether this entry exists only to preserve a configured service that was not rediscovered.</param>
    public InstalledServiceInfo(
        string serviceName,
        string displayName,
        string? executablePath,
        string? publisher,
        bool isAutomaticStart = false,
        bool isConfiguredOnly = false)
    {
        ServiceName = serviceName;
        DisplayName = displayName;
        ExecutablePath = executablePath;
        Publisher = publisher;
        IsAutomaticStart = isAutomaticStart;
        IsConfiguredOnly = isConfiguredOnly;
    }

    /// <summary>Gets the internal Windows service name used by AppSupervisor.</summary>
    public string ServiceName { get; }

    /// <summary>Gets the friendly service display name.</summary>
    public string DisplayName { get; }

    /// <summary>Gets the resolved service executable path, when Windows exposed one.</summary>
    public string? ExecutablePath { get; }

    /// <summary>Gets the executable's company name, when version metadata exposed one.</summary>
    public string? Publisher { get; }

    /// <summary>Gets whether the service currently uses Automatic startup.</summary>
    public bool IsAutomaticStart { get; }

    /// <summary>Gets whether this placeholder preserves an existing configuration absent from the discovered catalog.</summary>
    public bool IsConfiguredOnly { get; }
}
