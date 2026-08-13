using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using AppSupervisor.SteamVr;

namespace AppSupervisor;

static class Program
{
    private const int ErrorCancelled = 1223;
    private static Mutex? _singleInstanceMutex;

    /// <summary>
    /// Ensures the process is elevated, enforces a single instance, initializes WinForms, and starts the tray message loop.
    /// </summary>
    /// <param name="arguments">Private capture-host arguments or the ordinary tray invocation arguments.</param>
    [STAThread]
    static void Main(string[] arguments)
    {
        if (OpenVrSnapshotHost.TryRun(arguments))
            return;

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += ApplicationThreadException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += UnobservedTaskException;

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

        SupervisorLog.WriteInformation(
            $"AppSupervisor started. Version {Application.ProductVersion}; process " +
            $"{Environment.ProcessId}; executable '{Environment.ProcessPath}'; base directory " +
            $"'{AppContext.BaseDirectory}'."
        );

        try
        {
            Application.Run(new TrayApplicationContext());
            SupervisorLog.WriteInformation("The WinForms message loop ended normally.");
        }
        catch (Exception ex)
        {
            SupervisorLog.WriteError("The main application loop terminated unexpectedly.", ex);
            MessageBox.Show(
                $"AppSupervisor stopped because of an unexpected error.\n\n" +
                $"Details were saved to:\n{SupervisorLog.PathName}",
                "AppSupervisor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
        finally
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
    }

    /// <summary>Logs and contains otherwise-unhandled WinForms thread exceptions so the tray process can remain available.</summary>
    /// <param name="sender">The WinForms application infrastructure.</param>
    /// <param name="e">The exception raised by the UI message loop.</param>
    private static void ApplicationThreadException(
        object sender,
        ThreadExceptionEventArgs e)
    {
        SupervisorLog.WriteError("An unexpected WinForms thread error was contained.", e.Exception);
    }

    /// <summary>Logs an unhandled application-domain exception before the runtime terminates the process.</summary>
    /// <param name="sender">The current application domain.</param>
    /// <param name="e">The terminating exception information.</param>
    private static void CurrentDomainUnhandledException(
        object sender,
        UnhandledExceptionEventArgs e)
    {
        Exception exception = e.ExceptionObject as Exception ??
            new InvalidOperationException(e.ExceptionObject?.ToString() ?? "Unknown exception.");
        SupervisorLog.WriteError(
            e.IsTerminating
                ? "An unhandled exception is terminating AppSupervisor."
                : "An unhandled application-domain exception occurred.",
            exception
        );
    }

    /// <summary>Logs and observes a faulted background task that was never awaited by its owner.</summary>
    /// <param name="sender">The task infrastructure.</param>
    /// <param name="e">The unobserved aggregate exception.</param>
    private static void UnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        SupervisorLog.WriteError("An unobserved background task failed.", e.Exception);
        e.SetObserved();
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
