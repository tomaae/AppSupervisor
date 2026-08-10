using System.Text;
using System.Text.Json;

namespace AppSupervisor.Configuration;

/// <summary>
/// Validates and atomically writes complete configuration documents without exposing partial JSON files.
/// </summary>
public static class ConfigFileWriter
{
    /// <summary>Serializes a validated configuration using the same contract accepted by <see cref="ConfigLoader"/>.</summary>
    /// <param name="configuration">The complete supervisor-profile configuration.</param>
    /// <returns>Indented UTF-8 JSON text.</returns>
    public static string Serialize(IReadOnlyList<SupervisorProfileConfig> configuration)
    {
        Validate(configuration);
        return JsonSerializer.Serialize(configuration, ConfigJson.CreateOptions(writeIndented: true)) +
            Environment.NewLine;
    }

    /// <summary>Writes a complete validated configuration to a same-directory temporary file and atomically replaces the destination.</summary>
    /// <param name="path">The destination config.json path.</param>
    /// <param name="configuration">The complete configuration document.</param>
    public static void SaveAtomic(
        string path,
        IReadOnlyList<SupervisorProfileConfig> configuration)
    {
        string json = Serialize(configuration);
        string fullPath = Path.GetFullPath(path);
        string directoryPath = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The configuration directory could not be determined.");
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

    /// <summary>Runs semantic validation against a non-null view of the complete document.</summary>
    /// <param name="configuration">The configuration to validate.</param>
    private static void Validate(IReadOnlyList<SupervisorProfileConfig> configuration)
    {
        ConfigValidator.Validate(
            configuration
                .Select(profile => (SupervisorProfileConfig?)profile)
                .ToArray()
        );
    }
}
