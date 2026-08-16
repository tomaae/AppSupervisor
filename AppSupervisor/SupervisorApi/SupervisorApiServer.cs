using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppSupervisor.SupervisorApi;

/// <summary>Serves cached Supervisor state through a minimal loopback-only HTTP JSON listener.</summary>
internal sealed class SupervisorApiServer : IDisposable
{
    public const int Port = 17834;
    public const string BaseAddress = "http://127.0.0.1:17834/";
    private const int MaximumHeaderBytes = 8 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly object _lifecycleLock = new();
    private SupervisorApiSnapshot _snapshot = SupervisorApiSnapshot.Empty;
    private TcpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _acceptLoop;
    private bool _disposed;

    /// <summary>Atomically replaces the complete document available to API request threads.</summary>
    public void Publish(SupervisorApiSnapshot snapshot) =>
        Volatile.Write(ref _snapshot, snapshot);

    /// <summary>Starts or stops the fixed loopback listener to match global configuration.</summary>
    public void ApplyConfiguration(SupervisorApiConfig configuration)
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (configuration.Enabled)
                StartLocked();
            else
                StopLocked();
        }
    }

    /// <summary>Routes one request entirely against the last immutable timer snapshot.</summary>
    internal SupervisorApiResponse Route(string method, string rawPath)
    {
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            return Error(405, "methodNotAllowed", "The Supervisor API is read-only and accepts GET requests only.");

        string path = rawPath.Split('?', 2)[0];
        string[] segments;

        try
        {
            segments = path.Trim('/').Length == 0
                ? []
                : path.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        }
        catch (UriFormatException)
        {
            return Error(400, "invalidPath", "The request path contains invalid escaping.");
        }

        SupervisorApiSnapshot snapshot = Volatile.Read(ref _snapshot);

        if (segments.Length == 0)
        {
            return Json(200, new
            {
                snapshot.UpdatedUtc,
                snapshot.Paused,
                profiles = snapshot.Profiles.Select(profile => new
                {
                    profile.Name,
                    profile.InternalId,
                    profile.Enabled,
                    profile.Status,
                    endpoint = "/" + Uri.EscapeDataString(profile.InternalId)
                })
            });
        }

        SupervisorApiProfileSnapshot? selectedProfile = snapshot.Profiles.FirstOrDefault(profile =>
            string.Equals(profile.InternalId, segments[0], StringComparison.OrdinalIgnoreCase));
        if (selectedProfile is null)
            return Error(404, "profileNotFound", $"Profile '{segments[0]}' was not found.");

        string profileEndpoint = "/" + Uri.EscapeDataString(selectedProfile.InternalId);
        if (segments.Length == 1)
        {
            return Json(200, new
            {
                snapshot.UpdatedUtc,
                selectedProfile.Name,
                selectedProfile.InternalId,
                selectedProfile.Enabled,
                selectedProfile.Status,
                selectedProfile.MonitorProcess,
                helpers = selectedProfile.Helpers.Select(helper => new
                {
                    helper.Name,
                    helper.InternalId,
                    helper.Enabled,
                    helper.Active,
                    helper.Status,
                    healthChecksConfigured = helper.HealthChecks.Count,
                    macroActionsConfigured = helper.Macro.Actions.Count,
                    endpoint = profileEndpoint + "/" + Uri.EscapeDataString(helper.InternalId)
                })
            });
        }

        SupervisorApiHelperSnapshot? selectedHelper = selectedProfile.Helpers.FirstOrDefault(helper =>
            string.Equals(helper.InternalId, segments[1], StringComparison.OrdinalIgnoreCase));
        if (selectedHelper is null)
        {
            SupervisorApiHelperSnapshot[] nameMatches = selectedProfile.Helpers.Where(helper =>
                string.Equals(helper.Name, segments[1], StringComparison.OrdinalIgnoreCase)).ToArray();
            if (nameMatches.Length > 1)
                return Error(409, "helperNameAmbiguous", "Use the helper internalId shown by the profile endpoint.");
            selectedHelper = nameMatches.SingleOrDefault();
        }

        if (selectedHelper is null)
            return Error(404, "helperNotFound", $"Helper '{segments[1]}' was not found.");

        string helperEndpoint = profileEndpoint + "/" + Uri.EscapeDataString(selectedHelper.InternalId);
        if (segments.Length == 2)
        {
            return Json(200, new
            {
                snapshot.UpdatedUtc,
                profileInternalId = selectedProfile.InternalId,
                selectedHelper.Name,
                selectedHelper.InternalId,
                selectedHelper.Enabled,
                selectedHelper.Active,
                selectedHelper.Status,
                selectedHelper.Path,
                selectedHelper.AppUri,
                selectedHelper.Arguments,
                selectedHelper.Restart,
                selectedHelper.EnsureClosedUntilNeeded,
                selectedHelper.LeaveRunningAfterProfileStops,
                selectedHelper.MinimizeAfterStart,
                selectedHelper.MonitorResponsiveness,
                healthChecksConfigured = selectedHelper.HealthChecks.Count,
                macroActionsConfigured = selectedHelper.Macro.Actions.Count,
                healthCheckEndpoint = helperEndpoint + "/healthcheck",
                macroEndpoint = helperEndpoint + "/macro"
            });
        }

        if (segments.Length == 3 &&
            string.Equals(segments[2], "healthcheck", StringComparison.OrdinalIgnoreCase))
        {
            return Json(200, new
            {
                snapshot.UpdatedUtc,
                selectedProfile.InternalId,
                helperInternalId = selectedHelper.InternalId,
                healthChecks = selectedHelper.HealthChecks
            });
        }

        if (segments.Length == 3 &&
            string.Equals(segments[2], "macro", StringComparison.OrdinalIgnoreCase))
        {
            return Json(200, new
            {
                snapshot.UpdatedUtc,
                selectedProfile.InternalId,
                helperInternalId = selectedHelper.InternalId,
                selectedHelper.Macro.Configured,
                selectedHelper.Macro.Status,
                selectedHelper.Macro.Actions
            });
        }

        return Error(404, "endpointNotFound", "The requested Supervisor API endpoint was not found.");
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            StopLocked();
        }
    }

    private void StartLocked()
    {
        if (_listener is not null)
            return;

        var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start(backlog: 16);
        var cancellation = new CancellationTokenSource();
        _listener = listener;
        _cancellation = cancellation;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, cancellation.Token));
        SupervisorLog.WriteInformation($"Supervisor API listening at {BaseAddress}");
    }

    private void StopLocked()
    {
        TcpListener? listener = _listener;
        CancellationTokenSource? cancellation = _cancellation;
        _listener = null;
        _cancellation = null;
        _acceptLoop = null;
        cancellation?.Cancel();
        listener?.Stop();
        cancellation?.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);
                _ = HandleClientSafelyAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SupervisorLog.WriteError("Supervisor API listener stopped unexpectedly.", exception);
        }
    }

    private async Task HandleClientSafelyAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using NetworkStream stream = client.GetStream();
                byte[] buffer = new byte[MaximumHeaderBytes];
                int length = 0;

                while (length < buffer.Length)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                        return;
                    length += read;
                    if (ContainsHeaderTerminator(buffer.AsSpan(0, length)))
                        break;
                }

                string request = Encoding.ASCII.GetString(buffer, 0, length);
                string requestLine = request.Split("\r\n", 2)[0];
                string[] parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                SupervisorApiResponse response = parts.Length == 3
                    ? Route(parts[0], ExtractPath(parts[1]))
                    : Error(400, "invalidRequest", "A valid HTTP request line is required.");
                await WriteResponseAsync(stream, response, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
                // The local client disconnected before its one-shot response completed.
            }
            catch (Exception exception)
            {
                SupervisorLog.WriteError("Supervisor API request failed.", exception);
            }
        }
    }

    private static bool ContainsHeaderTerminator(ReadOnlySpan<byte> bytes)
    {
        for (int index = 3; index < bytes.Length; index++)
        {
            if (bytes[index - 3] == '\r' && bytes[index - 2] == '\n' &&
                bytes[index - 1] == '\r' && bytes[index] == '\n')
                return true;
        }
        return false;
    }

    private static string ExtractPath(string requestTarget)
    {
        return Uri.TryCreate(requestTarget, UriKind.Absolute, out Uri? absolute)
            ? absolute.PathAndQuery
            : requestTarget;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        SupervisorApiResponse response,
        CancellationToken cancellationToken)
    {
        string reason = response.StatusCode switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            405 => "Method Not Allowed",
            409 => "Conflict",
            _ => "Error"
        };
        string headers = $"HTTP/1.1 {response.StatusCode} {reason}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Cache-Control: no-store\r\n" +
            $"Content-Length: {response.Body.Length}\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken)
            .ConfigureAwait(false);
        await stream.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
    }

    private static SupervisorApiResponse Json(int statusCode, object payload) =>
        new(statusCode, JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), JsonOptions));

    private static SupervisorApiResponse Error(int statusCode, string code, string message) =>
        Json(statusCode, new { error = code, message });
}

internal sealed record SupervisorApiResponse(int StatusCode, byte[] Body)
{
    public string Json => Encoding.UTF8.GetString(Body);
}
