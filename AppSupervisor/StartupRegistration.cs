using System.Security.Principal;
using Microsoft.Win32;

namespace AppSupervisor;

/// <summary>
/// Validates and creates the elevated current-user Task Scheduler registration used to start AppSupervisor at sign-in.
/// </summary>
internal static class StartupRegistration
{
    private const string LegacyRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string TaskName = "AppSupervisor";
    private const string ValueName = "AppSupervisor";

    private static readonly WindowsStartupTaskScheduler Scheduler = new(TaskName);

    /// <summary>
    /// Checks whether the current executable has a correctly elevated current-user logon task.
    /// </summary>
    /// <returns><see langword="true"/> when the scheduled task exactly matches this executable and security context.</returns>
    public static bool IsEnabled()
    {
        string executablePath = GetExecutablePath();
        string userId = GetCurrentUserId();
        StartupTaskRegistration? registration = Scheduler.GetRegistration();
        bool enabled = IsMatchingRegistration(registration, executablePath, userId);

        if (enabled)
            RemoveLegacyRunRegistration();

        return enabled;
    }

    /// <summary>
    /// Registers the current executable as an elevated current-user logon task and removes the obsolete Run-key entry.
    /// </summary>
    public static void Enable()
    {
        string executablePath = GetExecutablePath();
        string workingDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException("The AppSupervisor executable directory could not be determined.");

        Scheduler.Register(executablePath, workingDirectory, GetCurrentUserId());
        RemoveLegacyRunRegistration();
    }

    /// <summary>
    /// Compares a discovered scheduled task with the exact executable, user, elevation, trigger, and duplicate-instance requirements.
    /// </summary>
    /// <param name="registration">The discovered Task Scheduler registration.</param>
    /// <param name="executablePath">The expected AppSupervisor executable path.</param>
    /// <param name="userId">The expected current-user SID.</param>
    /// <returns><see langword="true"/> only when every required startup property matches.</returns>
    internal static bool IsMatchingRegistration(
        StartupTaskRegistration? registration,
        string executablePath,
        string userId)
    {
        if (registration is null)
            return false;

        string? workingDirectory = Path.GetDirectoryName(executablePath);

        return registration.TaskEnabled &&
               registration.LogonTriggerEnabled &&
               registration.HighestPrivileges &&
               registration.IgnoreNewInstances &&
               registration.ActionCount == 1 &&
               registration.TriggerCount == 1 &&
               PathsEqual(registration.ExecutablePath, executablePath) &&
               PathsEqual(registration.WorkingDirectory, workingDirectory) &&
               string.Equals(
                   registration.PrincipalUserId,
                   userId,
                   StringComparison.OrdinalIgnoreCase
               ) &&
               string.Equals(
                   registration.TriggerUserId,
                   userId,
                   StringComparison.OrdinalIgnoreCase
               );
    }

    /// <summary>
    /// Compares two possibly differently formatted Windows paths after full-path normalization.
    /// </summary>
    /// <param name="left">The first possible path.</param>
    /// <param name="right">The second possible path.</param>
    /// <returns><see langword="true"/> when both paths identify the same Windows location.</returns>
    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase
            );
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves the current AppSupervisor executable path.
    /// </summary>
    /// <returns>The full executable path used by Task Scheduler.</returns>
    private static string GetExecutablePath()
    {
        string? executablePath = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidOperationException("The AppSupervisor executable path could not be determined.");

        return Path.GetFullPath(executablePath);
    }

    /// <summary>
    /// Resolves the current Windows user's stable security identifier for the task principal and trigger.
    /// </summary>
    /// <returns>The current user's SID string.</returns>
    private static string GetCurrentUserId()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();

        return identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows user SID could not be determined.");
    }

    /// <summary>
    /// Removes the legacy per-user Run entry after the scheduled task is confirmed or created.
    /// </summary>
    private static void RemoveLegacyRunRegistration()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(LegacyRunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
