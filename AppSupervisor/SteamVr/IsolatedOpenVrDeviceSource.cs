using System.Diagnostics;
using System.Text.Json;

namespace AppSupervisor.SteamVr;

/// <summary>
/// Captures OpenVR state through a short-lived child process so native runtime failure cannot
/// terminate the AppSupervisor tray process.
/// </summary>
internal sealed class IsolatedOpenVrDeviceSource : ISteamVrDeviceSource
{
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(10);
    private bool _disposed;

    /// <summary>Starts one isolated capture host and converts its JSON response into a snapshot.</summary>
    /// <returns>The captured SteamVR state or a contained source error.</returns>
    public SteamVrSnapshot Capture()
    {
        if (_disposed)
            return CreateFailure("The isolated SteamVR device source is disposed.");

        try
        {
            string executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException(
                    "The AppSupervisor executable path could not be determined."
                );
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(OpenVrSnapshotHost.CaptureArgument);

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Windows did not start the isolated OpenVR capture process."
                );
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)CaptureTimeout.TotalMilliseconds))
            {
                TryTerminate(process);
                return CreateFailure("The isolated OpenVR capture timed out.");
            }

            string output = outputTask.GetAwaiter().GetResult();
            string error = errorTask.GetAwaiter().GetResult();
            return ParseCaptureOutput(output, error, process.ExitCode, IsVrServerRunning());
        }
        catch (Exception ex)
        {
            return CreateFailure($"The isolated OpenVR capture could not run. {ex.Message}");
        }
    }

    /// <summary>Marks this stateless source unavailable for later captures.</summary>
    public void Dispose()
    {
        _disposed = true;
    }

    /// <summary>Parses a child snapshot, accepting valid output even if native cleanup later failed.</summary>
    /// <param name="output">The child process's standard output.</param>
    /// <param name="error">The child process's standard error.</param>
    /// <param name="exitCode">The child process exit code.</param>
    /// <param name="vrServerRunning">Whether vrserver remains present after capture.</param>
    /// <returns>The parsed snapshot or a contained diagnostic failure.</returns>
    internal static SteamVrSnapshot ParseCaptureOutput(
        string output,
        string error,
        int exitCode,
        bool vrServerRunning)
    {
        if (!string.IsNullOrWhiteSpace(output))
        {
            try
            {
                SteamVrSnapshot? snapshot = JsonSerializer.Deserialize<SteamVrSnapshot>(output);

                if (snapshot is not null)
                    return snapshot;
            }
            catch (JsonException)
            {
            }
        }

        string detail = string.IsNullOrWhiteSpace(error)
            ? $"The capture host exited with code {exitCode} without returning a snapshot."
            : $"The capture host exited with code {exitCode}. {error.Trim()}";
        return new SteamVrSnapshot(vrServerRunning, null, [], detail);
    }

    /// <summary>Best-effort terminates only the disposable capture process after its timeout.</summary>
    /// <param name="process">The isolated child process.</param>
    private static void TryTerminate(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(2000);
        }
        catch
        {
        }
    }

    /// <summary>Checks whether SteamVR's server process currently exists without retaining handles.</summary>
    /// <returns><see langword="true"/> when at least one vrserver process is running.</returns>
    private static bool IsVrServerRunning()
    {
        Process[] processes = Process.GetProcessesByName("vrserver");

        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }
    }

    /// <summary>Creates an error snapshot using the current SteamVR process state.</summary>
    /// <param name="message">The contained capture failure.</param>
    /// <returns>A snapshot that cannot throw into supervision.</returns>
    private static SteamVrSnapshot CreateFailure(string message)
        => new(IsVrServerRunning(), null, [], message);
}
