using System.Drawing;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AppSupervisor.StreamDeck;

namespace AppSupervisor.Tests;

/// <summary>Verifies the event-driven Stream Deck status protocol and tray-equivalent images.</summary>
public sealed class StreamDeckStatusClientTests
{
    [Fact]
    public async Task ConnectedServer_ReceivesLatestStatusAndCanRequestConfiguration()
    {
        string pipeName = $"AppSupervisor.StreamDeck.Tests.{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
        );
        var initial = CreateSnapshot(StreamDeckVisualState.Idle, "Starting");
        await using var client = new StreamDeckStatusClient(initial, pipeName);
        var configurationRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        client.ConfigurationRequested += () => configurationRequested.TrySetResult();
        var latest = CreateSnapshot(StreamDeckVisualState.Supervising, "Supervising");
        client.Publish(latest);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.WaitForConnectionAsync(timeout.Token);
        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(
            server,
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
        client.Publish(error);
        string? secondLine = await reader.ReadLineAsync(timeout.Token);
        Assert.NotNull(secondLine);
        using JsonDocument second = JsonDocument.Parse(secondLine);
        Assert.Equal("error", second.RootElement.GetProperty("state").GetString());
        Assert.Equal("Error", second.RootElement.GetProperty("title").GetString());

        await writer.WriteLineAsync(StreamDeckStatusClient.OpenConfigurationCommand);
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

    [Fact]
    public void ExtractExecutableIcon_AppHost_ReturnsRequestedHighResolutionIcon()
    {
        string appHostPath = Path.ChangeExtension(
            typeof(StreamDeckStatusImages).Assembly.Location,
            ".exe"
        );
        Assert.True(File.Exists(appHostPath), $"Expected app host was not found: {appHostPath}");

        using Icon icon = StreamDeckStatusImages.ExtractExecutableIcon(appHostPath, 144);

        Assert.Equal(new Size(144, 144), icon.Size);
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
