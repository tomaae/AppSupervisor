using System.Drawing;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AppSupervisor.StreamDeck;

namespace AppSupervisor.Tests;

/// <summary>Verifies the event-driven Stream Deck status protocol and tray-equivalent images.</summary>
public sealed class StreamDeckStatusServerTests
{
    [Fact]
    public async Task ConnectedClient_ReceivesLatestStatusAndCanRequestConfiguration()
    {
        string pipeName = $"AppSupervisor.StreamDeck.Tests.{Guid.NewGuid():N}";
        var initial = CreateSnapshot(StreamDeckVisualState.Idle, "Starting");
        await using var server = new StreamDeckStatusServer(initial, pipeName);
        var configurationRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        server.ConfigurationRequested += () => configurationRequested.TrySetResult();
        var latest = CreateSnapshot(StreamDeckVisualState.Supervising, "Supervising");
        server.Publish(latest);

        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(
            client,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            leaveOpen: true
        )
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        string? firstLine = await reader.ReadLineAsync(timeout.Token);
        Assert.NotNull(firstLine);
        using JsonDocument first = JsonDocument.Parse(firstLine);
        Assert.Equal(1, first.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("supervising", first.RootElement.GetProperty("state").GetString());
        Assert.Equal("Supervising", first.RootElement.GetProperty("title").GetString());

        var error = CreateSnapshot(StreamDeckVisualState.Error, "Error");
        server.Publish(error);
        string? secondLine = await reader.ReadLineAsync(timeout.Token);
        Assert.NotNull(secondLine);
        using JsonDocument second = JsonDocument.Parse(secondLine);
        Assert.Equal("error", second.RootElement.GetProperty("state").GetString());
        Assert.Equal("Error", second.RootElement.GetProperty("title").GetString());

        await writer.WriteLineAsync(StreamDeckStatusServer.OpenConfigurationCommand);
        await configurationRequested.Task.WaitAsync(timeout.Token);
    }

    [Fact]
    public void StatusImages_AreDistinctHighDpiPngDataUrls()
    {
        using Icon sourceIcon = (Icon)SystemIcons.Application.Clone();
        StreamDeckStatusImages images = StreamDeckStatusImages.Create(sourceIcon);
        var payloads = new HashSet<string>(StringComparer.Ordinal);

        foreach (StreamDeckVisualState state in Enum.GetValues<StreamDeckVisualState>())
        {
            string dataUrl = images[state];
            const string prefix = "data:image/png;base64,";
            Assert.StartsWith(prefix, dataUrl, StringComparison.Ordinal);
            byte[] bytes = Convert.FromBase64String(dataUrl[prefix.Length..]);
            Assert.True(payloads.Add(Convert.ToHexString(bytes)));

            using var stream = new MemoryStream(bytes);
            using Image image = Image.FromStream(stream);
            Assert.Equal(144, image.Width);
            Assert.Equal(144, image.Height);
        }
    }

    private static StreamDeckStatusSnapshot CreateSnapshot(
        StreamDeckVisualState state,
        string title)
    {
        return new StreamDeckStatusSnapshot(
            state,
            title,
            $"AppSupervisor - {title}",
            "data:image/png;base64,AA=="
        );
    }
}
