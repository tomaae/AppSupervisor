using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;

namespace AppSupervisor.NotificationHost;

internal static class Program
{
    private const string AppUserModelId = "AppSupervisor.Desktop";

    /// <summary>
    /// Decodes one notification request, registers its desktop identity, and submits it through inbox Windows PowerShell.
    /// </summary>
    /// <param name="args">The single Base64-encoded notification payload.</param>
    [STAThread]
    private static void Main(string[] args)
    {
        NotificationPayload? payload = TryDecodePayload(args);

        if (payload is null ||
            string.IsNullOrWhiteSpace(payload.MainExecutablePath) ||
            !File.Exists(payload.MainExecutablePath))
        {
            Environment.ExitCode = 2;
            return;
        }

        if (!StartMenuShortcutRegistrar.TryRegister(payload.MainExecutablePath))
        {
            Environment.ExitCode = 3;
            return;
        }

        Environment.ExitCode = TryShowNotification(payload)
            ? 0
            : 4;
    }

    /// <summary>
    /// Converts the Base64 command-line argument into validated notification data.
    /// </summary>
    /// <param name="args">The helper command-line arguments.</param>
    /// <returns>The decoded payload, or <see langword="null"/> when the request is malformed.</returns>
    private static NotificationPayload? TryDecodePayload(string[] args)
    {
        if (args.Length != 1)
            return null;

        try
        {
            byte[] jsonBytes = Convert.FromBase64String(args[0]);
            string json = Encoding.UTF8.GetString(jsonBytes);
            return JsonSerializer.Deserialize<NotificationPayload>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Runs hidden inbox Windows PowerShell and confirms that its notification script exits successfully.
    /// </summary>
    /// <param name="payload">The decoded notification content.</param>
    /// <returns><see langword="true"/> when PowerShell completes the notification script successfully.</returns>
    private static bool TryShowNotification(NotificationPayload payload)
    {
        try
        {
            string title = SecurityElement.Escape(payload.Title) ?? "AppSupervisor";
            string message = SecurityElement.Escape(payload.Message) ?? "";
            string toastXml =
                "<toast><visual><binding template=\"ToastGeneric\">" +
                $"<text>{title}</text><text>{message}</text>" +
                "</binding></visual></toast>";
            string xmlBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(toastXml));
            string script = CreatePowerShellScript(xmlBase64);
            string encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            string powerShellPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"
            );

            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = powerShellPath,
                Arguments = $"-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encodedScript}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            if (process is null)
                return false;

            if (process.WaitForExit(10_000))
                return process.ExitCode == 0;

            process.Kill(true);
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the PowerShell script that loads inbox WinRT notification types and displays decoded XML.
    /// </summary>
    /// <param name="xmlBase64">The UTF-8 toast XML encoded as Base64.</param>
    /// <returns>A script containing no unescaped user-provided text.</returns>
    private static string CreatePowerShellScript(string xmlBase64)
    {
        return
            "$ErrorActionPreference = 'Stop'\n" +
            "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null\n" +
            "[Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null\n" +
            "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] > $null\n" +
            $"$xmlText = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{xmlBase64}'))\n" +
            "$xml = New-Object Windows.Data.Xml.Dom.XmlDocument\n" +
            "$xml.LoadXml($xmlText)\n" +
            "$toast = New-Object Windows.UI.Notifications.ToastNotification $xml\n" +
            $"[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{AppUserModelId}').Show($toast)\n";
    }
}
