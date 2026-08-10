using AppSupervisor.Store;

namespace AppSupervisor.Configuration;

/// <summary>Refreshes versioned Windows package executable paths before configuration validation.</summary>
internal static class PackagedApplicationPathResolver
{
    /// <summary>Resolves every package-backed helper against the currently installed package version.</summary>
    /// <param name="configuration">The deserialized configuration document to update in memory.</param>
    public static void ResolvePaths(IEnumerable<SupervisorProfileConfig?> configuration)
    {
        ManagedApplicationConfig[] packagedApplications = configuration
            .Where(profile => profile is not null)
            .SelectMany(profile => profile!.Applications ?? [])
            .Where(IsPackageBacked)
            .ToArray();

        if (packagedApplications.Length == 0)
            return;

        IReadOnlyList<InstalledStoreApplication> installedApplications =
            WindowsStoreApplicationCatalog.LoadInstalledApplications();

        foreach (ManagedApplicationConfig application in packagedApplications)
        {
            InstalledStoreApplication? installed = installedApplications.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.PackageFamilyName,
                    application.PackageFamilyName,
                    StringComparison.OrdinalIgnoreCase
                ) &&
                string.Equals(
                    candidate.ApplicationId,
                    application.PackageApplicationId,
                    StringComparison.OrdinalIgnoreCase
                ) &&
                string.Equals(
                    candidate.ExecutableRelativePath,
                    application.PackageExecutable,
                    StringComparison.OrdinalIgnoreCase
                ));

            if (installed is not null)
                application.Path = installed.ExecutablePath;
        }
    }

    /// <summary>Checks whether all update-safe Windows package identity fields are present.</summary>
    /// <param name="application">The helper configuration to inspect.</param>
    /// <returns><see langword="true"/> when package path resolution should run.</returns>
    private static bool IsPackageBacked(ManagedApplicationConfig application)
    {
        return !string.IsNullOrWhiteSpace(application.PackageFamilyName) &&
            !string.IsNullOrWhiteSpace(application.PackageApplicationId) &&
            !string.IsNullOrWhiteSpace(application.PackageExecutable);
    }
}
