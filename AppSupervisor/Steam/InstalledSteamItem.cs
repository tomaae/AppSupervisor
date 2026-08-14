namespace AppSupervisor.Steam;

/// <summary>Describes one locally installed Steam library item and its content directory.</summary>
internal sealed record InstalledSteamItem(
    ulong AppId,
    string Name,
    string InstallDirectory,
    string LibraryDirectory)
{
    /// <summary>Gets the Steam protocol URI used to launch this installed item.</summary>
    public string AppUri => $"steam://rungameid/{AppId}";

    /// <summary>Gets the likely main executable used only to present this item with an icon.</summary>
    public string? IconExecutablePath { get; init; }
}
