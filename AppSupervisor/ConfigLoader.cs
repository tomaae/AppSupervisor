using System.Text.Json;
using AppSupervisor.Configuration;

namespace AppSupervisor;

/// <summary>
/// Loads strict JSON configuration documents and rejects invalid runtime semantics.
/// </summary>
public static class ConfigLoader
{
    /// <summary>
    /// Reads, deserializes, and semantically validates the supervisor-profile configuration from a JSON file.
    /// </summary>
    /// <param name="path">The path of the JSON configuration file.</param>
    /// <returns>The validated supervisor-profile configuration.</returns>
    public static List<SupervisorProfileConfig> Load(string path)
    {
        EnsureConfigurationExists(path);
        string json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<List<SupervisorProfileConfig?>>(
            json,
            ConfigJson.CreateOptions()
        );

        if (config is null)
            throw new ConfigValidationException(["The top-level JSON value must be an array."]);

        PackagedApplicationPathResolver.ResolvePaths(config);

        ConfigValidator.Validate(config);
        return config.Select(profile => profile!).ToList();
    }

    /// <summary>
    /// Creates a valid empty configuration document when no configuration file exists yet.
    /// </summary>
    /// <param name="path">The configuration path that must exist before reading.</param>
    private static void EnsureConfigurationExists(string path)
    {
        if (!File.Exists(path))
            ConfigFileWriter.SaveAtomic(path, []);
    }
}
