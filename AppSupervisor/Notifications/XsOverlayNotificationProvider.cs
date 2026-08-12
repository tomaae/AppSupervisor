using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AppSupervisor.Notifications;

/// <summary>
/// Sends notification commands to a running local XSOverlay WebSocket API instance.
/// </summary>
internal sealed class XsOverlayNotificationProvider : INotificationProvider, IDisposable
{
    private const string ProcessName = "XSOverlay";
    private const int DefaultWebSocketPort = 42070;
    private const int DeliveryAttempts = 3;
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _socketLock = new();
    private ClientWebSocket? _socket;
    private Uri? _connectedUri;
    private int _disposed;

    /// <summary>
    /// Gets the XSOverlay target handled by this provider.
    /// </summary>
    public NotificationTarget Target => NotificationTarget.XsOverlay;

    /// <summary>
    /// Reuses one XSOverlay session and sends a documented SendNotification command.
    /// </summary>
    /// <param name="notification">The notification content to display in VR.</param>
    /// <param name="cancellationToken">Cancels delivery during application shutdown.</param>
    /// <returns><see langword="true"/> when XSOverlay was running and accepted the WebSocket message.</returns>
    public async Task<bool> SendAsync(
        SupervisorNotification notification,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return false;

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return false;

            string? executablePath = FindRunningExecutablePath();
            int port = ResolveWebSocketPort(executablePath);
            Uri uri = CreateWebSocketUri(port);

            for (int attempt = 1; attempt <= DeliveryAttempts; attempt++)
            {
                if (await TrySendOnceAsync(
                    uri,
                    notification,
                    cancellationToken
                ).ConfigureAwait(false))
                {
                    return true;
                }

                if (attempt < DeliveryAttempts)
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }

            return false;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Sends through the current local connection and discards it after any transport failure.
    /// </summary>
    /// <param name="uri">The resolved local XSOverlay endpoint.</param>
    /// <param name="notification">The notification content to display in VR.</param>
    /// <param name="cancellationToken">Cancels delivery during application shutdown.</param>
    /// <returns><see langword="true"/> when this attempt sent the complete WebSocket message.</returns>
    private async Task<bool> TrySendOnceAsync(
        Uri uri,
        SupervisorNotification notification,
        CancellationToken cancellationToken)
    {
        try
        {
            ClientWebSocket socket = await GetConnectedSocketAsync(
                uri,
                cancellationToken
            ).ConfigureAwait(false);

            string json = CreateApiMessage(notification);
            byte[] bytes = Encoding.UTF8.GetBytes(json);

            using var timeoutCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(ConnectionTimeout);

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
            DiscardSocket();
            return false;
        }
        catch (WebSocketException)
        {
            DiscardSocket();
            return false;
        }
        catch (IOException)
        {
            DiscardSocket();
            return false;
        }
    }

    /// <summary>Reuses the active XSOverlay session or establishes one timeout-bounded replacement.</summary>
    /// <param name="uri">The current configured XSOverlay endpoint.</param>
    /// <param name="cancellationToken">Cancels connection establishment during shutdown.</param>
    /// <returns>An open provider-owned WebSocket.</returns>
    private async Task<ClientWebSocket> GetConnectedSocketAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        lock (_socketLock)
        {
            if (_socket?.State == WebSocketState.Open && _connectedUri == uri)
                return _socket;
        }

        DiscardSocket();
        ClientWebSocket replacement = CreateSocket();

        using var timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(ConnectionTimeout);

        try
        {
            await replacement.ConnectAsync(uri, timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch
        {
            replacement.Dispose();
            throw;
        }

        lock (_socketLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                replacement.Abort();
                replacement.Dispose();
                throw new OperationCanceledException(
                    "XSOverlay notification provider is shutting down."
                );
            }

            _socket = replacement;
            _connectedUri = uri;
            return replacement;
        }
    }

    /// <summary>Creates a direct local WebSocket with bounded protocol-level keepalive detection.</summary>
    /// <returns>A new unconnected WebSocket.</returns>
    private static ClientWebSocket CreateSocket()
    {
        var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.KeepAliveInterval = KeepAliveInterval;
        socket.Options.KeepAliveTimeout = KeepAliveTimeout;
        return socket;
    }

    /// <summary>Aborts and releases the current session after a transport failure.</summary>
    private void DiscardSocket()
    {
        ClientWebSocket? socket;

        lock (_socketLock)
        {
            socket = _socket;
            _socket = null;
            _connectedUri = null;
        }

        if (socket is null)
            return;

        try
        {
            socket.Abort();
        }
        finally
        {
            socket.Dispose();
        }
    }

    /// <summary>Stops new sends and aborts the persistent local WebSocket without waiting on XSOverlay.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        DiscardSocket();
    }

    /// <summary>
    /// Finds the full path of a running XSOverlay process when the current privilege boundary permits access.
    /// </summary>
    /// <returns>The running executable path, or <see langword="null"/> when it cannot be discovered.</returns>
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
    /// Builds the local XSOverlay endpoint without DNS or system-proxy discovery delays.
    /// </summary>
    /// <param name="port">The configured XSOverlay WebSocket port.</param>
    /// <returns>The loopback WebSocket URI with AppSupervisor's required client token.</returns>
    internal static Uri CreateWebSocketUri(int port)
    {
        return new Uri($"ws://127.0.0.1:{port}/?client=AppSupervisor");
    }

    /// <summary>
    /// Uses XSOverlay's configured WebSocket port when its installation path is accessible,
    /// otherwise falling back to the documented default port.
    /// </summary>
    /// <param name="executablePath">The discovered XSOverlay executable path, if accessible.</param>
    /// <returns>The configured valid port, or the documented default port.</returns>
    internal static int ResolveWebSocketPort(string? executablePath)
    {
        return string.IsNullOrWhiteSpace(executablePath)
            ? DefaultWebSocketPort
            : ReadConfiguredWebSocketPort(executablePath);
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
