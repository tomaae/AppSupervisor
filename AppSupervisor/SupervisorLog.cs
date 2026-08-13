namespace AppSupervisor;

/// <summary>
/// Writes best-effort diagnostic records for failures that cannot safely display a shutdown dialog.
/// </summary>
internal static class SupervisorLog
{
    private const long MaximumLogBytes = 1_048_576;
    private static readonly object SyncRoot = new();

    /// <summary>Gets the per-user diagnostic log path.</summary>
    public static string PathName => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppSupervisor",
        "AppSupervisor.log"
    );

    /// <summary>Appends one timestamped lifecycle record without allowing logging failures to escape.</summary>
    /// <param name="message">The diagnostic lifecycle detail.</param>
    public static void WriteInformation(string message)
    {
        WriteRecord("INFO", message);
    }

    /// <summary>Appends one timestamped exception without allowing logging failures to escape.</summary>
    public static void WriteError(string message, Exception exception)
    {
        WriteRecord(
            "ERROR",
            $"{message}{Environment.NewLine}{exception}"
        );
    }

    /// <summary>Appends and rotates one formatted diagnostic record under the shared writer lock.</summary>
    /// <param name="level">The diagnostic severity label.</param>
    /// <param name="message">The complete record body.</param>
    private static void WriteRecord(string level, string message)
    {
        try
        {
            lock (SyncRoot)
            {
                string path = PathName;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                if (File.Exists(path) && new FileInfo(path).Length > MaximumLogBytes)
                    File.Delete(path);

                File.AppendAllText(
                    path,
                    $"{DateTimeOffset.Now:O} {level} {message}" +
                    $"{Environment.NewLine}{Environment.NewLine}"
                );
            }
        }
        catch
        {
            // Logging must never interfere with supervision or shutdown.
        }
    }
}
