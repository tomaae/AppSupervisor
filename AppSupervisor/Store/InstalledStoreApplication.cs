namespace AppSupervisor.Store;

/// <summary>Describes one launchable application declared by an installed Windows package.</summary>
internal sealed record InstalledStoreApplication(
    string DisplayName,
    string PackageName,
    string PackageFamilyName,
    string ApplicationId,
    string ExecutableRelativePath,
    string ExecutablePath,
    bool IsMicrosoftOrSystem)
{
    /// <summary>Gets the Explorer AppsFolder target used to launch this packaged application.</summary>
    public string AppUri => $"shell:AppsFolder\\{PackageFamilyName}!{ApplicationId}";
}
