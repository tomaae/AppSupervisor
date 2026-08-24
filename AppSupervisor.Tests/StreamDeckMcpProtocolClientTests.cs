using AppSupervisor.StreamDeck;

namespace AppSupervisor.Tests;

/// <summary>Verifies the deterministic MCP messages used with Elgato MCP Server.</summary>
public sealed class StreamDeckMcpProtocolClientTests
{
    [Fact]
    public async Task LoadActions_FiltersOtherAppsAndToolsRequiringArguments()
    {
        const string responses =
            "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"serverInfo\":{\"name\":\"Elgato\",\"version\":\"1\"}}}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":\"2\",\"result\":{\"tools\":[" +
            "{\"name\":\"streamdeck__start_vr\",\"title\":\"Start VR\",\"description\":\"Starts VR\",\"inputSchema\":{\"type\":\"object\"}}," +
            "{\"name\":\"other__action\",\"title\":\"Other\",\"inputSchema\":{\"type\":\"object\"}}," +
            "{\"name\":\"streamdeck__needs_input\",\"title\":\"Input\",\"inputSchema\":{\"type\":\"object\",\"required\":[\"value\"]}}]}}\n";
        using var reader = new StringReader(responses);
        using var writer = new StringWriter();
        var client = new StreamDeckMcpProtocolClient(reader, writer);

        await client.InitializeAsync(CancellationToken.None);
        IReadOnlyList<StreamDeckMcpAction> actions = await client.LoadActionsAsync(
            CancellationToken.None
        );

        StreamDeckMcpAction action = Assert.Single(actions);
        Assert.Equal("streamdeck__start_vr", action.ToolName);
        Assert.Equal("Start VR", action.DisplayName);
        Assert.Contains("notifications/initialized", writer.ToString());
        Assert.Contains("tools/list", writer.ToString());
    }

    [Fact]
    public async Task ExecuteAction_SendsEmptyArgumentsToSelectedTool()
    {
        const string response =
            "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}}\n";
        using var reader = new StringReader(response);
        using var writer = new StringWriter();
        var client = new StreamDeckMcpProtocolClient(reader, writer);

        await client.ExecuteActionAsync("streamdeck__start_vr", CancellationToken.None);

        Assert.Contains("\"method\":\"tools/call\"", writer.ToString());
        Assert.Contains("\"name\":\"streamdeck__start_vr\"", writer.ToString());
        Assert.Contains("\"arguments\":{}", writer.ToString());
    }
}
