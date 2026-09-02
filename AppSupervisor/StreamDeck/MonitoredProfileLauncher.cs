using System.Diagnostics;

namespace AppSupervisor.StreamDeck;

/// <summary>Launches a process-triggered profile target without duplicating a running process.</summary>
internal static class MonitoredProfileLauncher
{
    internal static MonitoredProfileLaunchOutcome Launch(SupervisorProfileConfig profile) =>
        Launch(profile, IsRunning, startInfo => Process.Start(startInfo));

    internal static MonitoredProfileLaunchOutcome Launch(
        SupervisorProfileConfig profile,
        Func<string, bool> isRunning,
        Func<ProcessStartInfo, Process?> startProcess)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string target = profile.MonitorProcess?.Trim() ?? "";

        if (!profile.Enabled)
            throw new InvalidOperationException($"Profile '{profile.Name}' is disabled.");
        if (profile.TriggerType != ProfileTriggerType.Process)
        {
            throw new InvalidOperationException(
                $"Profile '{profile.Name}' does not use a process trigger."
            );
        }
        if (target.Length == 0)
            throw new InvalidOperationException($"Profile '{profile.Name}' has no monitored process.");
        if (isRunning(target))
            return MonitoredProfileLaunchOutcome.AlreadyRunning;

        var startInfo = new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true
        };

        if (Path.IsPathFullyQualified(target))
        {
            if (!File.Exists(target))
            {
                throw new FileNotFoundException(
                    $"The monitored executable for profile '{profile.Name}' does not exist.",
                    target
                );
            }

            startInfo.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(target))!;
        }

        using Process? process = startProcess(startInfo);
        return MonitoredProfileLaunchOutcome.Started;
    }

    private static bool IsRunning(string processPathOrName)
    {
        string processName = Path.GetFileNameWithoutExtension(processPathOrName);
        Process[] processes = Process.GetProcessesByName(processName);

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
}

internal enum MonitoredProfileLaunchOutcome
{
    Started,
    AlreadyRunning
}
