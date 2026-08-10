using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AppSupervisor.Notifications;

/// <summary>
/// Sends notification commands to a running local XSOverlay WebSocket API instance.
/// </summary>
internal sealed class XsOverlayNotificationProvider : INotificationProvider
{
    private const string ProcessName = "XSOverlay";
    private const int DefaultWebSocketPort = 42070;
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Gets the XSOverlay target handled by this provider.
    /// </summary>
    public NotificationTarget Target => NotificationTarget.XsOverlay;

    /// <summary>
    /// Connects to XSOverlay on localhost and sends one documented SendNotification command.
    /// </summary>
    /// <param name="notification">The notification content to display in VR.</param>
    /// <param name="cancellationToken">Cancels delivery during application shutdown.</param>
    /// <returns><see langword="true"/> when XSOverlay was running and accepted the WebSocket message.</returns>
    public async Task<bool> SendAsync(
        SupervisorNotification notification,
        CancellationToken cancellationToken)
    {
        string? executablePath = FindRunningExecutablePath();

        if (executablePath is null)
            return false;

        int port = ReadConfiguredWebSocketPort(executablePath);
        var uri = new Uri($"ws://localhost:{port}/?client=AppSupervisor");

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(ConnectionTimeout);
        using var socket = new ClientWebSocket();

        try
        {
            await socket.ConnectAsync(uri, timeoutCancellation.Token).ConfigureAwait(false);

            string json = CreateApiMessage(notification);
            byte[] bytes = Encoding.UTF8.GetBytes(json);

            await socket.SendAsync(
                bytes,
                WebSocketMessageType.Text,
                endOfMessage: true,
                timeoutCancellation.Token
            ).ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (WebSocketException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds a running XSOverlay process and returns its full executable path when accessible.
    /// </summary>
    /// <returns>The running executable path, or <see langword="null"/> when XSOverlay is unavailable.</returns>
    private static string? FindRunningExecutablePath()
    {
        Process[] processes = Process.GetProcessesByName(ProcessName);

        try
        {
            foreach (Process process in processes)
            {
                try
                {
                    string? path = process.MainModule?.FileName;

                    if (!string.IsNullOrWhiteSpace(path))
                        return path;
                }
                catch
                {
                    // An inaccessible process cannot be used for reliable XSOverlay delivery.
                }
            }
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }

        return null;
    }

    /// <summary>
    /// Reads XSOverlay's adjacent API configuration so a customized WebSocket port needs no AppSupervisor option.
    /// </summary>
    /// <param name="executablePath">The full path of the running XSOverlay executable.</param>
    /// <returns>The configured valid port, or the documented default port when it cannot be read.</returns>
    private static int ReadConfiguredWebSocketPort(string executablePath)
    {
        try
        {
            string installationDirectory = Path.GetDirectoryName(executablePath)
                ?? throw new InvalidOperationException("XSOverlay has no installation directory.");
            string configPath = Path.Combine(
                installationDirectory,
                "XSOverlay_Data",
                "StreamingAssets",
                "Plugins",
                "Config",
                "ExternalMessageAPIConfig.json"
            );

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath));

            if (document.RootElement.TryGetProperty("WebSocketPort", out JsonElement portElement) &&
                portElement.TryGetInt32(out int port) &&
                port is > 0 and <= 65535)
            {
                return port;
            }
        }
        catch
        {
            // XSOverlay's documented default remains usable when its optional configuration cannot be read.
        }

        return DefaultWebSocketPort;
    }

    /// <summary>
    /// Serializes the XSOverlay API envelope and its nested WebSocket notification object.
    /// </summary>
    /// <param name="notification">The provider-independent notification content.</param>
    /// <returns>The JSON message sent over the local WebSocket.</returns>
    private static string CreateApiMessage(SupervisorNotification notification)
    {
        string style = notification.Severity switch
        {
            NotificationSeverity.Warning => "warning",
            NotificationSeverity.Error => "error",
            _ => "default"
        };

        string notificationJson = JsonSerializer.Serialize(new
        {
            type = 1,
            index = 0,
            timeout = notification.Severity == NotificationSeverity.Information ? 3.0f : 5.0f,
            height = 175.0f,
            opacity = 1.0f,
            volume = 0.7f,
            audioPath = style,
            title = notification.Title,
            content = notification.Message,
            useBase64Icon = false,
            icon = style,
            sourceApp = "AppSupervisor"
        });

        return JsonSerializer.Serialize(new
        {
            sender = "AppSupervisor",
            target = "xsoverlay",
            command = "SendNotification",
            jsonData = notificationJson,
            rawData = (string?)null
        });
    }
}
