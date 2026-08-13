using System.Text.Json;

namespace AppSupervisor.SteamVr;

/// <summary>Runs unsafe native OpenVR capture only inside a disposable child process.</summary>
internal static class OpenVrSnapshotHost
{
    internal const string CaptureArgument = "--capture-openvr-snapshot";

    /// <summary>Handles the private capture command before the tray application initializes.</summary>
    /// <param name="arguments">The process command-line arguments excluding the executable path.</param>
    /// <returns><see langword="true"/> when this process was invoked as the capture host.</returns>
    public static bool TryRun(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 1 ||
            !string.Equals(arguments[0], CaptureArgument, StringComparison.Ordinal))
        {
            return false;
        }

        SteamVrSnapshot snapshot;

        using (var source = new OpenVrDeviceSource())
        {
            snapshot = source.Capture();
            WriteSnapshot(snapshot);
        }

        return true;
    }

    /// <summary>Writes one UTF-8 JSON snapshot to redirected standard output before native cleanup begins.</summary>
    /// <param name="snapshot">The completed OpenVR snapshot.</param>
    private static void WriteSnapshot(SteamVrSnapshot snapshot)
    {
        using Stream output = Console.OpenStandardOutput();
        JsonSerializer.Serialize(output, snapshot);
        output.Flush();
    }
}
