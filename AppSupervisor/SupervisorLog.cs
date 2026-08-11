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

    /// <summary>Appends one timestamped exception without allowing logging failures to escape.</summary>
    public static void WriteError(string message, Exception exception)
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
                    $"{DateTimeOffset.Now:O} ERROR {message}{Environment.NewLine}" +
                    $"{exception}{Environment.NewLine}{Environment.NewLine}"
                );
            }
        }
        catch
        {
            // Logging must never interfere with supervision or shutdown.
        }
    }
}
