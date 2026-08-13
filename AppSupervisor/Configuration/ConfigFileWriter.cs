using System.Text;
using System.Text.Json;

namespace AppSupervisor.Configuration;

/// <summary>Validates and atomically writes complete configuration documents.</summary>
public static class ConfigFileWriter
{
    /// <summary>Serializes a validated configuration using the strict loader contract.</summary>
    public static string Serialize(AppSupervisorConfig configuration)
    {
        Validate(configuration);
        return JsonSerializer.Serialize(configuration, ConfigJson.CreateOptions(writeIndented: true)) +
            Environment.NewLine;
    }

    /// <summary>Writes a complete document to a same-directory temporary file and atomically replaces the destination.</summary>
    public static void SaveAtomic(string path, AppSupervisorConfig configuration)
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

    private static void Validate(AppSupervisorConfig configuration)
    {
        if (configuration.Profiles is null)
            throw new ConfigValidationException(["The configuration must contain a profiles array."]);
        if (configuration.Integrations is null)
            throw new ConfigValidationException(["The configuration must contain an integrations object."]);

        ConfigValidator.Validate(configuration.Profiles);
        IntegrationConfigValidator.Validate(configuration.Integrations, configuration.Profiles);
    }
}
