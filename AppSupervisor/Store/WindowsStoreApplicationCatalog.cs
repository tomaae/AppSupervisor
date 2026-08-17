using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using AppSupervisor.Discovery;

namespace AppSupervisor.Store;

/// <summary>Discovers launchable applications from packages installed for the current Windows user.</summary>
internal static class WindowsStoreApplicationCatalog
{
    private const int DiscoveryTimeoutMilliseconds = 15000;

    /// <summary>Runs the Windows package query and parses every launchable application manifest.</summary>
    /// <returns>Installed applications ordered by display name and package family.</returns>
    public static IReadOnlyList<InstalledStoreApplication> LoadInstalledApplications()
    {
        return ApplicationDiscoveryRetry.Execute(
            "Windows Store",
            LoadInstalledApplicationsCore
        );
    }

    /// <summary>Performs one complete Windows package query and manifest parsing attempt.</summary>
    /// <returns>Installed applications ordered by display name and package family.</returns>
    private static IReadOnlyList<InstalledStoreApplication> LoadInstalledApplicationsCore()
    {
        string json = QueryInstalledPackages();

        if (string.IsNullOrWhiteSpace(json))
            return [];

        using JsonDocument document = JsonDocument.Parse(json);
        IEnumerable<JsonElement> packageElements = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : [document.RootElement];
        var applications = new List<InstalledStoreApplication>();

        foreach (JsonElement package in packageElements)
        {
            if (ReadBoolean(package, "IsFramework") ||
                ReadBoolean(package, "IsResourcePackage"))
            {
                continue;
            }

            string? packageName = ReadString(package, "Name");
            string? familyName = ReadString(package, "PackageFamilyName");
            string? installLocation = ReadString(package, "InstallLocation");

            if (string.IsNullOrWhiteSpace(packageName) ||
                string.IsNullOrWhiteSpace(familyName) ||
                string.IsNullOrWhiteSpace(installLocation))
            {
                continue;
            }

            applications.AddRange(ParseManifest(
                Path.Combine(installLocation, "AppxManifest.xml"),
                packageName,
                familyName,
                installLocation,
                ReadBoolean(package, "NonRemovable")
            ));
        }

        return applications
            .GroupBy(application => application.AppUri, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(application => application.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(application => application.PackageFamilyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Parses one package manifest into its launchable executable applications.</summary>
    /// <param name="manifestPath">The AppxManifest.xml path.</param>
    /// <param name="packageName">The installed package identity name.</param>
    /// <param name="packageFamilyName">The installed package family name.</param>
    /// <param name="installLocation">The current version's package installation directory.</param>
    /// <param name="nonRemovable">Whether Windows marks the package as a system component.</param>
    /// <returns>Launchable applications with existing executable paths.</returns>
    internal static IReadOnlyList<InstalledStoreApplication> ParseManifest(
        string manifestPath,
        string packageName,
        string packageFamilyName,
        string installLocation,
        bool nonRemovable)
    {
        try
        {
            XDocument document = XDocument.Load(manifestPath);
            XElement? properties = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "Properties");
            string packageDisplayName = properties?.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "DisplayName")?.Value
                ?? packageName;
            string publisherDisplayName = properties?.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "PublisherDisplayName")?.Value
                ?? "";
            bool isMicrosoftOrSystem = nonRemovable ||
                packageName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
                publisherDisplayName.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
            var applications = new List<InstalledStoreApplication>();

            foreach (XElement application in document.Descendants()
                .Where(element => element.Name.LocalName == "Application"))
            {
                string? applicationId = application.Attribute("Id")?.Value;
                string? executableRelativePath = application.Attribute("Executable")?.Value;

                if (string.IsNullOrWhiteSpace(applicationId) ||
                    string.IsNullOrWhiteSpace(executableRelativePath))
                {
                    continue;
                }

                string executablePath = Path.GetFullPath(Path.Combine(
                    installLocation,
                    executableRelativePath.Replace('\\', Path.DirectorySeparatorChar)
                ));

                if (!File.Exists(executablePath))
                    continue;

                XElement? visualElements = application.Elements()
                    .FirstOrDefault(element => element.Name.LocalName == "VisualElements");
                string displayName = visualElements?.Attribute("DisplayName")?.Value
                    ?? packageDisplayName;

                if (displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                    displayName = packageDisplayName;

                applications.Add(new InstalledStoreApplication(
                    displayName,
                    packageName,
                    packageFamilyName,
                    applicationId,
                    executableRelativePath,
                    executablePath,
                    isMicrosoftOrSystem
                ));
            }

            return applications;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Queries package metadata using the built-in Windows PowerShell AppX provider.</summary>
    /// <returns>Compact JSON containing package identity and installation fields.</returns>
    private static string QueryInstalledPackages()
    {
        string powerShellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe"
        );
        var startInfo = new ProcessStartInfo
        {
            FileName = powerShellPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "Get-AppxPackage | Select-Object Name,PackageFamilyName,InstallLocation," +
            "IsFramework,IsResourcePackage,NonRemovable | ConvertTo-Json -Compress -Depth 3"
        );

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows PowerShell could not be started.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(DiscoveryTimeoutMilliseconds))
        {
            process.Kill(true);
            throw new TimeoutException("Windows Store application discovery timed out.");
        }

        string standardOutput = output.GetAwaiter().GetResult();
        string standardError = error.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Windows package discovery failed: {standardError.Trim()}");

        return standardOutput;
    }

    /// <summary>Reads one optional string property from a package query row.</summary>
    /// <param name="element">The package JSON object.</param>
    /// <param name="propertyName">The property to read.</param>
    /// <returns>The string value, or <see langword="null"/> when unavailable.</returns>
    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    /// <summary>Reads one optional Boolean property from a package query row.</summary>
    /// <param name="element">The package JSON object.</param>
    /// <param name="propertyName">The property to read.</param>
    /// <returns>The Boolean value, or <see langword="false"/> when unavailable.</returns>
    private static bool ReadBoolean(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) &&
            value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            value.GetBoolean();
    }
}
