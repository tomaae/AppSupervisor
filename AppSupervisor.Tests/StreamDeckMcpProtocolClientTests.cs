using AppSupervisor.StreamDeck;

namespace AppSupervisor.Tests;

/// <summary>Verifies the deterministic MCP messages used with Elgato MCP Server.</summary>
public sealed class StreamDeckMcpProtocolClientTests
{
    [Fact]
    public async Task LoadActions_ParsesExecutableActionsReturnedByStreamDeck()
    {
        const string responses =
            "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"serverInfo\":{\"name\":\"Elgato\",\"version\":\"1\"}}}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":\"2\",\"result\":{\"content\":[{\"type\":\"text\",\"text\":" +
            "\"{\\\"actions\\\":[{\\\"id\\\":\\\"action-2\\\",\\\"description\\\":{\\\"name\\\":\\\"Start VR\\\",\\\"description\\\":\\\"Starts VR\\\"}},{\\\"id\\\":\\\"action-1\\\",\\\"description\\\":{\\\"name\\\":\\\"Action Bar\\\",\\\"description\\\":\\\"Shows Action Bar\\\"}}]}\"}]}}\n";
        using var reader = new StringReader(responses);
        using var writer = new StringWriter();
        var client = new StreamDeckMcpProtocolClient(reader, writer);

        await client.InitializeAsync(CancellationToken.None);
        IReadOnlyList<StreamDeckMcpAction> actions = await client.LoadActionsAsync(
            CancellationToken.None
        );

        Assert.Equal(2, actions.Count);
        Assert.Equal("action-1", actions[0].ActionId);
        Assert.Equal("Action Bar", actions[0].DisplayName);
        Assert.Equal("action-2", actions[1].ActionId);
        Assert.Equal("Start VR", actions[1].DisplayName);
        Assert.Contains("notifications/initialized", writer.ToString());
        Assert.Contains("streamdeck__get_executable_actions", writer.ToString());
    }

    [Fact]
    public async Task ExecuteAction_SendsEmptyArgumentsToSelectedTool()
    {
        const string response =
            "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}}\n";
        using var reader = new StringReader(response);
        using var writer = new StringWriter();
        var client = new StreamDeckMcpProtocolClient(reader, writer);

        await client.ExecuteActionAsync("action-2", CancellationToken.None);

        Assert.Contains("\"method\":\"tools/call\"", writer.ToString());
        Assert.Contains("\"name\":\"streamdeck__execute_action\"", writer.ToString());
        Assert.Contains("\"arguments\":{\"id\":\"action-2\"}", writer.ToString());
    }
}
