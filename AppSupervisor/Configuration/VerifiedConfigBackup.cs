namespace AppSupervisor.Configuration;

/// <summary>
/// Saves the last verified in-memory configuration to the stable config.json.old shutdown backup.
/// </summary>
public static class VerifiedConfigBackup
{
    /// <summary>Atomically writes a verified configuration beside its live file using the .old suffix.</summary>
    /// <param name="configPath">The live config.json path.</param>
    /// <param name="configuration">The last successfully loaded configuration model.</param>
    /// <returns>The absolute backup path.</returns>
    public static string Save(
        string configPath,
        AppSupervisorConfig configuration)
    {
        string backupPath = Path.GetFullPath(configPath) + ".old";
        ConfigFileWriter.SaveAtomic(backupPath, configuration);
        return backupPath;
    }
}
