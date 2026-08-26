using System.Text;
using System.Text.Json;

namespace AppSupervisor.Configuration;

/// <summary>Exports and imports one profile without application-wide integration settings.</summary>
public static class ProfileTransferService
{
    /// <summary>The preferred portable-profile filename suffix.</summary>
    public const string FileSuffix = ".appsupervisor-profile.json";

    internal const string DocumentFormat = "AppSupervisor.Profile";
    internal const int DocumentVersion = 1;
    internal const long MaximumImportBytes = 4 * 1024 * 1024;

    /// <summary>Serializes one validated profile into the strict portable document format.</summary>
    public static ProfileExportResult Serialize(SupervisorProfileConfig profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        SupervisorProfileConfig exportProfile = ConfigJson.Clone(profile);
        ConfigNormalizer.Normalize([exportProfile]);
        ConfigValidator.ValidatePortableProfile(exportProfile);
        IReadOnlyList<string> warnings = ProfilePortabilityAnalyzer.Analyze(exportProfile);
        var document = new ProfileTransferDocument
        {
            Format = DocumentFormat,
            Version = DocumentVersion,
            PortabilityWarnings = [.. warnings],
            Profile = exportProfile
        };
        string json = JsonSerializer.Serialize(
            document,
            ConfigJson.CreateOptions(writeIndented: true)
        ) + Environment.NewLine;
        return new ProfileExportResult(json, warnings);
    }

    /// <summary>Atomically writes one portable profile document.</summary>
    public static ProfileExportResult SaveAtomic(string path, SupervisorProfileConfig profile)
    {
        ProfileExportResult result = Serialize(profile);
        WriteAtomic(path, result.Json);
        return result;
    }

    /// <summary>Loads, validates, and prepares one imported profile as a disabled new profile.</summary>
    public static ProfileImportResult Load(
        string path,
        IReadOnlyList<SupervisorProfileConfig> existingProfiles)
    {
        string json = ReadBounded(path);
        return Deserialize(json, existingProfiles);
    }

    /// <summary>Validates a portable document and prepares a collision-free disabled profile.</summary>
    public static ProfileImportResult Deserialize(
        string json,
        IReadOnlyList<SupervisorProfileConfig> existingProfiles)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(existingProfiles);

        ProfileTransferDocument document;

        try
        {
            document = JsonSerializer.Deserialize<ProfileTransferDocument>(
                json,
                ConfigJson.CreateOptions()
            ) ?? throw new ProfileTransferException("The profile export document cannot be null.");
        }
        catch (JsonException exception)
        {
            throw new ProfileTransferException(
                $"The selected file is not a valid AppSupervisor profile export: {exception.Message}",
                exception
            );
        }

        if (!string.Equals(document.Format, DocumentFormat, StringComparison.Ordinal))
        {
            throw new ProfileTransferException(
                $"The selected file is not an {DocumentFormat} export."
            );
        }

        if (document.Version != DocumentVersion)
        {
            throw new ProfileTransferException(
                $"Profile export version {document.Version} is not supported; expected version {DocumentVersion}."
            );
        }

        if (document.PortabilityWarnings is null)
            throw new ProfileTransferException("The profile export must contain a portabilityWarnings array.");
        if (document.PortabilityWarnings.Any(string.IsNullOrWhiteSpace))
        {
            throw new ProfileTransferException(
                "The profile export contains an invalid portability warning."
            );
        }
        if (document.Profile is null)
            throw new ProfileTransferException("The profile export must contain one profile object.");

        SupervisorProfileConfig imported = ConfigJson.Clone(document.Profile);
        ConfigNormalizer.Normalize([imported]);

        try
        {
            ConfigValidator.ValidatePortableProfile(imported);
        }
        catch (ConfigValidationException exception)
        {
            throw new ProfileTransferException(
                $"The selected file contains an invalid profile: {exception.Message}",
                exception
            );
        }

        string originalName = imported.Name;
        imported.Name = CreateUniqueImportedName(imported.Name, existingProfiles);
        RegenerateIdentities(imported, existingProfiles);
        imported.Enabled = false;

        IReadOnlyList<string> warnings = ProfilePortabilityAnalyzer.Analyze(imported);
        return new ProfileImportResult(
            imported,
            warnings,
            NameChanged: !string.Equals(originalName, imported.Name, StringComparison.Ordinal)
        );
    }

    /// <summary>Creates a filesystem-safe suggested export filename.</summary>
    public static string CreateSuggestedFileName(string profileName)
    {
        string trimmed = profileName?.Trim() ?? "";
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        string safeName = new(trimmed.Select(character =>
            invalid.Contains(character) || char.IsControl(character) ? '_' : character
        ).ToArray());
        safeName = safeName.Trim().TrimEnd('.');

        if (safeName.Length == 0)
            safeName = "Profile";
        if (safeName.Length > 120)
            safeName = safeName[..120].TrimEnd();

        return safeName + FileSuffix;
    }

    private static void RegenerateIdentities(
        SupervisorProfileConfig profile,
        IReadOnlyList<SupervisorProfileConfig> existingProfiles)
    {
        var reservedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (SupervisorProfileConfig existing in existingProfiles)
        {
            if (!string.IsNullOrWhiteSpace(existing.ProfileId))
                reservedIds.Add(existing.ProfileId.Trim());

            foreach (ManagedResourceConfig resource in EnumerateResources(existing))
            {
                if (!string.IsNullOrWhiteSpace(resource.ResourceId))
                    reservedIds.Add(resource.ResourceId.Trim());
            }
        }

        profile.ProfileId = GenerateUniqueId(reservedIds);
        var resourceIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<ManagedResourceConfig> importedResources = [.. EnumerateResources(profile)];

        foreach (ManagedResourceConfig resource in importedResources)
        {
            string previousId = resource.ResourceId.Trim();
            string replacementId = GenerateUniqueId(reservedIds);
            resourceIdMap.Add(previousId, replacementId);
            resource.ResourceId = replacementId;
        }

        foreach (ManagedResourceConfig resource in importedResources)
        {
            string dependencyId = resource.DependencyResourceId?.Trim() ?? "";
            resource.DependencyResourceId = dependencyId.Length == 0
                ? ""
                : resourceIdMap[dependencyId];
        }
    }

    private static string GenerateUniqueId(ISet<string> reservedIds)
    {
        string candidate;

        do
        {
            candidate = Guid.NewGuid().ToString("N");
        }
        while (!reservedIds.Add(candidate));

        return candidate;
    }

    private static string CreateUniqueImportedName(
        string preferredName,
        IReadOnlyList<SupervisorProfileConfig> existingProfiles)
    {
        var existingNames = existingProfiles
            .Select(profile => profile.Name?.Trim() ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!existingNames.Contains(preferredName))
            return preferredName;

        string importedName = $"{preferredName} (Imported)";

        if (!existingNames.Contains(importedName))
            return importedName;

        for (int suffix = 2; ; suffix++)
        {
            string candidate = $"{preferredName} (Imported {suffix})";

            if (!existingNames.Contains(candidate))
                return candidate;
        }
    }

    internal static IEnumerable<ManagedResourceConfig> EnumerateResources(
        SupervisorProfileConfig profile)
    {
        return profile.Applications.Cast<ManagedResourceConfig>()
            .Concat(profile.Services)
            .Concat(profile.Delays)
            .Concat(profile.HomeAssistantResources)
            .Concat(profile.MqttResources)
            .Concat(profile.ObsResources)
            .Concat(profile.StreamDeckResources)
            .Concat(profile.TwitchResources)
            .Concat(profile.AudioInterfaces);
    }

    private static string ReadBounded(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete
        );

        if (stream.Length > MaximumImportBytes)
        {
            throw new ProfileTransferException(
                $"The selected profile export is larger than the {MaximumImportBytes / (1024 * 1024)} MB limit."
            );
        }

        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true
        );
        string json = reader.ReadToEnd();

        if (stream.Length > MaximumImportBytes)
        {
            throw new ProfileTransferException(
                $"The selected profile export is larger than the {MaximumImportBytes / (1024 * 1024)} MB limit."
            );
        }

        return json;
    }

    private static void WriteAtomic(string path, string json)
    {
        string fullPath = Path.GetFullPath(path);
        string directoryPath = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The profile export directory could not be determined.");
        Directory.CreateDirectory(directoryPath);
        string temporaryPath = Path.Combine(
            directoryPath,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp"
        );

        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            using (var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}

/// <summary>Contains serialized profile JSON and its environment-specific warnings.</summary>
public sealed record ProfileExportResult(string Json, IReadOnlyList<string> Warnings);

/// <summary>Contains one validated disabled import and its presentation metadata.</summary>
public sealed record ProfileImportResult(
    SupervisorProfileConfig Profile,
    IReadOnlyList<string> Warnings,
    bool NameChanged);

/// <summary>Reports a malformed or unsupported portable-profile document.</summary>
public sealed class ProfileTransferException : Exception
{
    public ProfileTransferException(string message) : base(message)
    {
    }

    public ProfileTransferException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class ProfileTransferDocument
{
    public string? Format { get; set; }
    public int Version { get; set; }
    public List<string>? PortabilityWarnings { get; set; }
    public SupervisorProfileConfig? Profile { get; set; }
}

internal static class ProfilePortabilityAnalyzer
{
    internal const string TriggerWarning =
        "The activation process name must match an application installed on the importing computer.";
    internal const string ApplicationWarning =
        "Helper executable paths, launch URIs, arguments, and Windows package identities may need to be reselected on another computer.";
    internal const string ServiceWarning =
        "Windows service names must exist on the importing computer before the profile is enabled.";
    internal const string HealthCheckWarning =
        "Health checks may reference computer-specific ports, process names, or VRChat OSC parameters.";
    internal const string MonitorWarning =
        "Window macros preserve monitor device names and coordinates; review them for the importing display layout.";
    internal const string AudioWarning =
        "Windows audio actions preserve endpoint and hardware identifiers; reselect unavailable devices before enabling the profile.";
    internal const string IntegrationWarning =
        "Home Assistant, MQTT, OBS, Stream Deck, and Twitch resources require application-wide integration settings; credentials and connection settings are intentionally not included.";

    public static IReadOnlyList<string> Analyze(SupervisorProfileConfig profile)
    {
        var warnings = new List<string> { TriggerWarning };

        if (profile.Applications.Count > 0)
            warnings.Add(ApplicationWarning);
        if (profile.Services.Count > 0)
            warnings.Add(ServiceWarning);
        if (profile.Applications.Any(application => application.HealthChecks.Count > 0))
            warnings.Add(HealthCheckWarning);
        if (profile.Applications.Any(application => application.StartupMacros.Any(action =>
            action.Type is StartupMacroActionType.MoveWindow or StartupMacroActionType.ResizeWindow)))
        {
            warnings.Add(MonitorWarning);
        }
        if (profile.AudioInterfaces.Any(resource => !resource.UseDefaultDevice))
            warnings.Add(AudioWarning);
        if (profile.HomeAssistantResources.Count > 0 || profile.MqttResources.Count > 0 ||
            profile.ObsResources.Count > 0 ||
            profile.StreamDeckResources.Count > 0 || profile.TwitchResources.Count > 0)
        {
            warnings.Add(IntegrationWarning);
        }

        return warnings;
    }
}
