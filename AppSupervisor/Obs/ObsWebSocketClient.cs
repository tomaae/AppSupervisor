using System.Buffers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AppSupervisor.Obs;

/// <summary>Implements the OBS WebSocket 5.x JSON protocol using the .NET WebSocket client.</summary>
internal sealed class ObsWebSocketClient : IObsWebSocketClient
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly ObsIntegrationConfig _configuration;

    public ObsWebSocketClient(ObsIntegrationConfig configuration)
    {
        _configuration = configuration;
    }

    public async Task ExecuteActionAsync(
        ObsResourceConfig configuration,
        CancellationToken cancellationToken)
    {
        await using ObsConnection connection = await ConnectAsync(cancellationToken)
            .ConfigureAwait(false);

        switch (configuration.Action)
        {
            case ObsActionType.SwitchScene:
                await connection.SendRequestAsync(
                    "SetCurrentProgramScene",
                    new { sceneName = configuration.SceneName },
                    cancellationToken
                ).ConfigureAwait(false);
                break;

            case ObsActionType.SetInputMute:
                await connection.SendRequestAsync(
                    "SetInputMute",
                    new
                    {
                        inputName = configuration.InputName,
                        inputMuted = configuration.Muted
                    },
                    cancellationToken
                ).ConfigureAwait(false);
                break;

            case ObsActionType.SetSourceVisibility:
                JsonElement sceneItem = await connection.SendRequestAsync(
                    "GetSceneItemId",
                    new
                    {
                        sceneName = configuration.SceneName,
                        sourceName = configuration.SourceName
                    },
                    cancellationToken
                ).ConfigureAwait(false);
                int sceneItemId = RequireProperty(sceneItem, "sceneItemId").GetInt32();
                await connection.SendRequestAsync(
                    "SetSceneItemEnabled",
                    new
                    {
                        sceneName = configuration.SceneName,
                        sceneItemId,
                        sceneItemEnabled = configuration.Visible
                    },
                    cancellationToken
                ).ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported OBS action type '{configuration.Action}'."
                );
        }
    }

    public async Task<ObsCatalog> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        await using ObsConnection connection = await ConnectAsync(cancellationToken)
            .ConfigureAwait(false);
        JsonElement sceneResponse = await connection.SendRequestAsync(
            "GetSceneList",
            requestData: null,
            cancellationToken
        ).ConfigureAwait(false);
        JsonElement inputResponse = await connection.SendRequestAsync(
            "GetInputList",
            requestData: null,
            cancellationToken
        ).ConfigureAwait(false);

        string[] scenes = RequireProperty(sceneResponse, "scenes")
            .EnumerateArray()
            .Select(scene => RequireProperty(scene, "sceneName").GetString() ?? "")
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        string[] discoveredInputs = RequireProperty(inputResponse, "inputs")
            .EnumerateArray()
            .Select(input => RequireProperty(input, "inputName").GetString() ?? "")
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var inputs = new List<string>();

        foreach (string inputName in discoveredInputs)
        {
            try
            {
                await connection.SendRequestAsync(
                    "GetInputMute",
                    new { inputName },
                    cancellationToken
                ).ConfigureAwait(false);
                inputs.Add(inputName);
            }
            catch (ObsRequestException)
            {
                // OBS rejects GetInputMute for inputs that have no audio capability.
            }
        }

        var sources = new List<ObsSceneSource>();

        foreach (string sceneName in scenes)
        {
            JsonElement itemResponse = await connection.SendRequestAsync(
                "GetSceneItemList",
                new { sceneName },
                cancellationToken
            ).ConfigureAwait(false);

            foreach (JsonElement item in RequireProperty(itemResponse, "sceneItems").EnumerateArray())
            {
                string sourceName = RequireProperty(item, "sourceName").GetString() ?? "";

                if (sourceName.Length > 0)
                    sources.Add(new ObsSceneSource(sceneName, sourceName));
            }
        }

        return new ObsCatalog(
            connection.ObsWebSocketVersion,
            scenes,
            inputs,
            sources
                .DistinctBy(
                    source => $"{source.SceneName}\0{source.SourceName}",
                    StringComparer.OrdinalIgnoreCase
                )
                .OrderBy(source => source.SceneName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(source => source.SourceName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray()
        );
    }

    internal static async Task<ObsCatalog> LoadCatalogAsync(
        ObsIntegrationConfig configuration,
        CancellationToken cancellationToken)
    {
        using var client = new ObsWebSocketClient(configuration);
        return await client.LoadCatalogAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
    }

    internal static string CreateAuthenticationResponse(
        string password,
        string salt,
        string challenge)
    {
        string secret = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(password + salt))
        );
        return Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge))
        );
    }

    internal static Uri CreateEndpoint(ObsIntegrationConfig configuration)
    {
        try
        {
            return new UriBuilder("ws", configuration.Host.Trim(), configuration.Port).Uri;
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            throw new InvalidOperationException("The OBS WebSocket endpoint is invalid.", ex);
        }
    }

    private async Task<ObsConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var connection = new ObsConnection();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectionTimeout);

        try
        {
            await connection.ConnectAsync(
                CreateEndpoint(_configuration),
                _configuration.Password,
                timeout.Token
            ).ConfigureAwait(false);
            return connection;
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new TimeoutException("OBS WebSocket did not respond within 10 seconds.");
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static JsonElement RequireProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
            throw new InvalidOperationException(
                $"OBS WebSocket response did not contain '{propertyName}'."
            );

        return property;
    }

    private sealed class ObsConnection : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private int _nextRequestId;

        public string ObsWebSocketVersion { get; private set; } = "unknown";

        public async Task ConnectAsync(
            Uri endpoint,
            string password,
            CancellationToken cancellationToken)
        {
            await _socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            JsonElement hello = await ReceivePayloadAsync(expectedOp: 0, cancellationToken)
                .ConfigureAwait(false);
            ObsWebSocketVersion = hello.TryGetProperty(
                "obsWebSocketVersion",
                out JsonElement version)
                ? version.GetString() ?? "unknown"
                : "unknown";
            var identify = new Dictionary<string, object?>
            {
                ["rpcVersion"] = 1
            };

            if (hello.TryGetProperty("authentication", out JsonElement authentication))
            {
                string challenge = RequireProperty(authentication, "challenge").GetString() ?? "";
                string salt = RequireProperty(authentication, "salt").GetString() ?? "";

                if (string.IsNullOrEmpty(password))
                {
                    throw new InvalidOperationException(
                        "OBS WebSocket requires a password, but the configured password is empty."
                    );
                }

                identify["authentication"] = CreateAuthenticationResponse(
                    password,
                    salt,
                    challenge
                );
            }

            await SendEnvelopeAsync(1, identify, cancellationToken).ConfigureAwait(false);
            await ReceivePayloadAsync(expectedOp: 2, cancellationToken).ConfigureAwait(false);
        }

        public async Task<JsonElement> SendRequestAsync(
            string requestType,
            object? requestData,
            CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            try
            {
                return await SendRequestCoreAsync(requestType, requestData, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"OBS request '{requestType}' did not respond within 10 seconds."
                );
            }
        }

        private async Task<JsonElement> SendRequestCoreAsync(
            string requestType,
            object? requestData,
            CancellationToken cancellationToken)
        {
            string requestId = Interlocked.Increment(ref _nextRequestId).ToString(
                System.Globalization.CultureInfo.InvariantCulture
            );
            var payload = new Dictionary<string, object?>
            {
                ["requestType"] = requestType,
                ["requestId"] = requestId
            };

            if (requestData is not null)
                payload["requestData"] = requestData;

            await SendEnvelopeAsync(6, payload, cancellationToken).ConfigureAwait(false);

            while (true)
            {
                JsonElement response = await ReceiveEnvelopeAsync(cancellationToken)
                    .ConfigureAwait(false);
                int op = RequireProperty(response, "op").GetInt32();

                if (op != 7)
                    continue;

                JsonElement data = RequireProperty(response, "d");
                string receivedId = RequireProperty(data, "requestId").GetString() ?? "";

                if (!string.Equals(receivedId, requestId, StringComparison.Ordinal))
                    continue;

                JsonElement status = RequireProperty(data, "requestStatus");

                if (!RequireProperty(status, "result").GetBoolean())
                {
                    int code = status.TryGetProperty("code", out JsonElement codeElement)
                        ? codeElement.GetInt32()
                        : 0;
                    string comment = status.TryGetProperty("comment", out JsonElement commentElement)
                        ? commentElement.GetString() ?? "Request rejected."
                        : "Request rejected.";
                    throw new ObsRequestException(requestType, code, comment);
                }

                return data.TryGetProperty("responseData", out JsonElement responseData)
                    ? responseData.Clone()
                    : JsonSerializer.SerializeToElement(new { });
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await _socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "AppSupervisor request completed",
                        CancellationToken.None
                    ).ConfigureAwait(false);
                }
                catch
                {
                    // Disposal must not replace the useful request result or exception.
                }
            }

            _socket.Dispose();
        }

        private async Task<JsonElement> ReceivePayloadAsync(
            int expectedOp,
            CancellationToken cancellationToken)
        {
            JsonElement envelope = await ReceiveEnvelopeAsync(cancellationToken)
                .ConfigureAwait(false);
            int op = RequireProperty(envelope, "op").GetInt32();

            if (op != expectedOp)
            {
                throw new InvalidOperationException(
                    $"OBS WebSocket returned operation {op}; expected {expectedOp}."
                );
            }

            return RequireProperty(envelope, "d").Clone();
        }

        private async Task<JsonElement> ReceiveEnvelopeAsync(CancellationToken cancellationToken)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);

            try
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken)
                        .ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        throw new InvalidOperationException(
                            $"OBS WebSocket closed the connection: {_socket.CloseStatusDescription ?? "no reason supplied"}."
                        );
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                        throw new InvalidOperationException("OBS WebSocket returned a non-text message.");

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                message.Position = 0;
                using JsonDocument document = await JsonDocument.ParseAsync(
                    message,
                    cancellationToken: cancellationToken
                ).ConfigureAwait(false);
                return document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "OBS WebSocket returned malformed JSON.",
                    ex
                );
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private async Task SendEnvelopeAsync(
            int op,
            object payload,
            CancellationToken cancellationToken)
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { op, d = payload });
            await _socket.SendAsync(
                json,
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken
            ).ConfigureAwait(false);
        }
    }

    private sealed class ObsRequestException(
        string requestType,
        int statusCode,
        string comment) : InvalidOperationException(
            $"OBS request '{requestType}' failed ({statusCode}): {comment}"
        );
}
