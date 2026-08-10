using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppSupervisor.Configuration;

/// <summary>
/// Provides one consistent strict JSON contract for loading, cloning, and saving configuration models.
/// </summary>
public static class ConfigJson
{
    /// <summary>Creates serializer options using camel-case property and enum names.</summary>
    /// <param name="writeIndented">Whether saved JSON should be human-readable and indented.</param>
    /// <returns>A fresh options instance safe for one configuration operation.</returns>
    public static JsonSerializerOptions CreateOptions(bool writeIndented = false)
    {
        return new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = writeIndented,
            Converters =
            {
                new JsonStringEnumConverter(
                    JsonNamingPolicy.CamelCase,
                    allowIntegerValues: false
                )
            }
        };
    }

    /// <summary>Creates a detached deep copy of a configuration model through its JSON contract.</summary>
    /// <typeparam name="T">The configuration model type.</typeparam>
    /// <param name="value">The model to clone.</param>
    /// <returns>A detached clone.</returns>
    public static T Clone<T>(T value)
    {
        string json = JsonSerializer.Serialize(value, CreateOptions());
        return JsonSerializer.Deserialize<T>(json, CreateOptions())
            ?? throw new InvalidOperationException("Configuration cloning returned null.");
    }
}
