using System.Reflection;
using System.Runtime.InteropServices;

namespace AppSupervisor.Notifications;

/// <summary>
/// Uses the interactive Windows shell to start a process at the desktop user's normal integrity level.
/// </summary>
internal static class UnelevatedProcessLauncher
{
    /// <summary>
    /// Requests Explorer to launch an executable without inheriting AppSupervisor's administrator token.
    /// </summary>
    /// <param name="executablePath">The full executable path to launch.</param>
    /// <param name="arguments">The command-line arguments passed to the executable.</param>
    /// <returns><see langword="true"/> when the shell accepts the launch request.</returns>
    public static bool TryLaunch(string executablePath, string arguments)
    {
        object? shell = null;

        try
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");

            if (shellType is null)
                return false;

            shell = Activator.CreateInstance(shellType);

            if (shell is null)
                return false;

            shellType.InvokeMember(
                "ShellExecute",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args:
                [
                    executablePath,
                    arguments,
                    Path.GetDirectoryName(executablePath) ?? "",
                    "open",
                    0
                ]
            );

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell))
                Marshal.FinalReleaseComObject(shell);
        }
    }
}
