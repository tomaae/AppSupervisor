namespace AppSupervisor.Configuration;

/// <summary>
/// Resolves a bundled Java runtime for a strongly identified Launch4j wrapper without repeatedly
/// inspecting processes or scanning ordinary helper executables.
/// </summary>
internal static class JavaLauncherDetector
{
    private const int MaximumHeaderScanBytes = 256 * 1024;
    private static ReadOnlySpan<byte> Launch4jMarker => "Launch4j"u8;
    private static ReadOnlySpan<byte> JavawMarker => "bin\\javaw.exe"u8;

    /// <summary>
    /// Returns a validated bundled javaw.exe for a Launch4j wrapper, otherwise the configured path.
    /// </summary>
    /// <param name="configuredPath">The executable selected for launching.</param>
    /// <returns>The exact executable path that represents the persistent helper runtime.</returns>
    internal static string ResolveRuntimePath(string configuredPath)
    {
        try
        {
            string launcherPath = Path.GetFullPath(configuredPath);
            string? launcherDirectory = Path.GetDirectoryName(launcherPath);

            if (launcherDirectory is null)
                return launcherPath;

            string runtimePath = Path.Combine(
                launcherDirectory,
                "jre",
                "bin",
                "javaw.exe"
            );

            // Ordinary applications avoid file scanning entirely.
            if (!File.Exists(runtimePath) || !File.Exists(launcherPath))
                return launcherPath;

            using var stream = new FileStream(
                launcherPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan
            );
            int requestedBytes = checked((int)Math.Min(stream.Length, MaximumHeaderScanBytes));
            byte[] header = new byte[requestedBytes];
            int bytesRead = 0;

            while (bytesRead < requestedBytes)
            {
                int read = stream.Read(header, bytesRead, requestedBytes - bytesRead);

                if (read == 0)
                    break;

                bytesRead += read;
            }

            ReadOnlySpan<byte> content = header.AsSpan(0, bytesRead);

            if (content.IndexOf(Launch4jMarker) < 0 || content.IndexOf(JavawMarker) < 0)
                return launcherPath;

            SupervisorLog.WriteTrace(
                $"Application launcher '{launcherPath}' uses bundled Launch4j runtime " +
                $"'{runtimePath}'."
            );
            return runtimePath;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException or
                IOException or UnauthorizedAccessException)
        {
            return configuredPath;
        }
    }
}
