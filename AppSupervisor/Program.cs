using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace AppSupervisor;

static class Program
{
    private const int ErrorCancelled = 1223;
    private static Mutex? _singleInstanceMutex;

    /// <summary>
    /// Ensures the process is elevated, enforces a single instance, initializes WinForms, and starts the tray message loop.
    /// </summary>
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        if (!EnsureRunningAsAdministrator())
            return;

        const string mutexName = "AppSupervisor.SingleInstance";

        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: mutexName,
            createdNew: out bool createdNew
        );

        if (!createdNew)
        {
            MessageBox.Show(
                "AppSupervisor is already running.",
                "AppSupervisor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );

            return;
        }

        Application.Run(new TrayApplicationContext());

        _singleInstanceMutex.ReleaseMutex();
        _singleInstanceMutex.Dispose();
    }

    /// <summary>
    /// Verifies that the current process has an administrator token and requests UAC elevation by relaunching when necessary.
    /// </summary>
    /// <returns><see langword="true"/> when the current process is elevated; otherwise, <see langword="false"/> after relaunch or cancellation.</returns>
    private static bool EnsureRunningAsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);

        if (principal.IsInRole(WindowsBuiltInRole.Administrator))
            return true;

        try
        {
            string? executablePath = Environment.ProcessPath;

            if (string.IsNullOrWhiteSpace(executablePath))
                throw new InvalidOperationException("The AppSupervisor executable path could not be determined.");

            using Process? elevatedProcess = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"AppSupervisor requires administrator rights and could not request elevation.\n\n{ex.Message}",
                "AppSupervisor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }

        return false;
    }
}
