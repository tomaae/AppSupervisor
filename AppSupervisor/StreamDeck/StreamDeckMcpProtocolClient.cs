using System.Text.Json;

namespace AppSupervisor.StreamDeck;

/// <summary>Implements the small sequential MCP subset required by Elgato MCP Server.</summary>
internal sealed class StreamDeckMcpProtocolClient(TextReader reader, TextWriter writer)
{
    private const string ProtocolVersion = "2025-06-18";
    private const string DiscoverActionsTool = "streamdeck__get_executable_actions";
    private const string ExecuteActionTool = "streamdeck__execute_action";
    private readonly TextReader _reader = reader;
    private readonly TextWriter _writer = writer;
    private long _nextRequestId;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await SendRequestAsync(
            "initialize",
            new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new { },
                clientInfo = new
                {
                    name = "AppSupervisor",
                    version = typeof(StreamDeckMcpProtocolClient).Assembly.GetName().Version?
                        .ToString() ?? "1.0.0"
                }
            },
            cancellationToken
        ).ConfigureAwait(false);
        await SendNotificationAsync("notifications/initialized", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StreamDeckMcpAction>> LoadActionsAsync(
        CancellationToken cancellationToken)
    {
        JsonElement result = await CallToolAsync(
            DiscoverActionsTool,
            new { },
            cancellationToken
        ).ConfigureAwait(false);
        string actionCatalogJson = ReadToolResultText(result);

        using JsonDocument catalog = JsonDocument.Parse(actionCatalogJson);
        if (!catalog.RootElement.TryGetProperty("actions", out JsonElement actionsJson) ||
            actionsJson.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Stream Deck returned no executable actions list.");

        var actions = new List<StreamDeckMcpAction>();

        foreach (JsonElement action in actionsJson.EnumerateArray())
        {
            string id = ReadString(action, "id");
            if (id.Length == 0)
                continue;

            JsonElement descriptionObject = action.TryGetProperty(
                "description",
                out JsonElement value
            ) && value.ValueKind == JsonValueKind.Object
                ? value
                : default;
            string title = descriptionObject.ValueKind == JsonValueKind.Object
                ? ReadString(descriptionObject, "name")
                : "";
            string description = descriptionObject.ValueKind == JsonValueKind.Object
                ? ReadString(descriptionObject, "description")
                : "";
            actions.Add(new StreamDeckMcpAction(
                id,
                title.Length == 0 ? "Unnamed Stream Deck action" : title,
                description
            ));
        }

        return actions
            .OrderBy(action => action.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(action => action.ActionId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task ExecuteActionAsync(
        string actionId,
        CancellationToken cancellationToken)
    {
        await CallToolAsync(
            ExecuteActionTool,
            new { id = actionId },
            cancellationToken
        ).ConfigureAwait(false);
    }

    private async Task<JsonElement> CallToolAsync(
        string toolName,
        object arguments,
        CancellationToken cancellationToken)
    {
        JsonElement result = await SendRequestAsync(
            "tools/call",
            new { name = toolName, arguments },
            cancellationToken
        ).ConfigureAwait(false);
        if (result.TryGetProperty("isError", out JsonElement isError) &&
            isError.ValueKind == JsonValueKind.True)
        {
            throw new InvalidOperationException(ReadToolResultText(result));
        }

        return result;
    }

    private async Task<JsonElement> SendRequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        string id = Interlocked.Increment(ref _nextRequestId).ToString(
            System.Globalization.CultureInfo.InvariantCulture
        );
        await WriteMessageAsync(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters
        }, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            string? line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                throw new IOException("Elgato MCP Server closed its output stream.");

            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("method", out JsonElement incomingMethod))
            {
                if (string.Equals(
                    incomingMethod.GetString(),
                    "elicitation/create",
                    StringComparison.Ordinal) &&
                    root.TryGetProperty("id", out JsonElement elicitationId))
                {
                    await WriteMessageAsync(new
                    {
                        jsonrpc = "2.0",
                        id = ReadId(elicitationId),
                        result = new { action = "decline" }
                    }, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            if (!root.TryGetProperty("id", out JsonElement responseId) ||
                !string.Equals(ReadId(responseId), id, StringComparison.Ordinal))
            {
                continue;
            }

            if (root.TryGetProperty("error", out JsonElement error))
            {
                string message = ReadString(error, "message");
                throw new InvalidOperationException(
                    message.Length == 0 ? "Elgato MCP Server returned an error." : message
                );
            }

            if (!root.TryGetProperty("result", out JsonElement result))
                throw new InvalidOperationException("Elgato MCP Server returned no result.");

            return result.Clone();
        }
    }

    private Task SendNotificationAsync(string method, CancellationToken cancellationToken) =>
        WriteMessageAsync(new
        {
            jsonrpc = "2.0",
            method
        }, cancellationToken);

    private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(message);
        await _writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ReadToolResultText(JsonElement result)
    {
        if (result.TryGetProperty("content", out JsonElement content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in content.EnumerateArray())
            {
                string text = ReadString(item, "text");
                if (text.Length > 0)
                    return text;
            }
        }

        return "The Stream Deck action reported a failure.";
    }

    private static string ReadString(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";

    private static string ReadId(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.String => id.GetString() ?? "",
        JsonValueKind.Number => id.GetRawText(),
        _ => ""
    };
}
