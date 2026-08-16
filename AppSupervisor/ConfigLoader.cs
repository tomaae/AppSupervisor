using System.Text.Json;
using AppSupervisor.Configuration;

namespace AppSupervisor;

/// <summary>
/// Loads strict JSON configuration documents and rejects invalid runtime semantics.
/// </summary>
public static class ConfigLoader
{
    /// <summary>Loads and validates the complete configuration document.</summary>
    public static AppSupervisorConfig Load(string path)
    {
        EnsureConfigurationExists(path);
        string json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<AppSupervisorConfig>(
            json,
            ConfigJson.CreateOptions()
        );

        if (config is null)
            throw new ConfigValidationException(["The top-level configuration cannot be null."]);
        if (config.Profiles is null)
            throw new ConfigValidationException(["The configuration must contain a profiles array."]);
        if (config.Integrations is null)
            throw new ConfigValidationException(["The configuration must contain an integrations object."]);

        ConfigMigration.MigrateLegacyStartupWaits(config.Profiles);
        ConfigNormalizer.Normalize(config.Profiles);
        NormalizeIntegrations(config.Integrations);
        PackagedApplicationPathResolver.ResolvePaths(config.Profiles);
        ConfigValidator.Validate(config.Profiles);
        IntegrationConfigValidator.Validate(config.Integrations, config.Profiles);
        return config;
    }

    /// <summary>Loads and validates configuration on a worker thread.</summary>
    public static Task<AppSupervisorConfig> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Load(path), cancellationToken);

    private static void EnsureConfigurationExists(string path)
    {
        if (!File.Exists(path))
            ConfigFileWriter.SaveAtomic(path, new AppSupervisorConfig());
    }

    private static void NormalizeIntegrations(IntegrationsConfig integrations)
    {
        if (integrations.HomeAssistant is not null)
        {
            integrations.HomeAssistant.Url = integrations.HomeAssistant.Url?.Trim() ?? "";
            integrations.HomeAssistant.Token = integrations.HomeAssistant.Token?.Trim() ?? "";
        }

        if (integrations.Obs is not null)
        {
            integrations.Obs.Host = integrations.Obs.Host?.Trim() ?? "";
            integrations.Obs.Password ??= "";
        }

        if (integrations.SteamVr?.Devices is null)
            return;

        foreach (SteamVrDeviceConfig? device in integrations.SteamVr.Devices)
        {
            if (device is null)
                continue;

            device.SerialNumber = device.SerialNumber?.Trim() ?? "";
            device.Name = device.Name?.Trim() ?? "";
            device.ModelNumber = device.ModelNumber?.Trim() ?? "";
        }
    }
}
