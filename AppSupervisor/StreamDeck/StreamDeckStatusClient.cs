using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace AppSupervisor.StreamDeck;

/// <summary>
/// Pushes deduplicated tray presentations to the current user's Stream Deck plugin and receives
/// its configuration-open command without polling or exposing a network listener.
/// </summary>
internal sealed class StreamDeckStatusClient : IAsyncDisposable
{
    public const string PipeName = "AppSupervisor.StreamDeck.v1";
    public const int ProtocolVersion = 1;
    public const string OpenConfigurationCommand = "openConfiguration";
    public const string LaunchProfileCommandPrefix = "launchProfile:";
    private const int MaximumCommandLength = 256;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private static readonly byte[] NewLine = [(byte)'\n'];

    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<bool> _statusChanged = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        }
    );
    private readonly Task _clientTask;
    private StreamDeckStatusSnapshot _snapshot;
    private int _disposed;

    public StreamDeckStatusClient(
        StreamDeckStatusSnapshot initialSnapshot,
        string pipeName = PipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _snapshot = initialSnapshot;
        _pipeName = pipeName;
        _clientTask = Task.Run(() => RunClientAsync(_shutdown.Token));
    }

    /// <summary>Raised when the user presses a companion Stream Deck Status action.</summary>
    public event Action? ConfigurationRequested;

    /// <summary>Raised when a launch action requests one configured process profile.</summary>
    public event Action<string>? ProfileLaunchRequested;

    /// <summary>Stores the latest presentation and wakes a connected writer only when it changed.</summary>
    public void Publish(StreamDeckStatusSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        StreamDeckStatusSnapshot previous = Volatile.Read(ref _snapshot);
        if (previous == snapshot)
            return;

        Volatile.Write(ref _snapshot, snapshot);
        _statusChanged.Writer.TryWrite(true);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _shutdown.Cancel();

        try
        {
            await _clientTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private async Task RunClientAsync(CancellationToken cancellationToken)
    {
        TimeSpan retryDelay = TimeSpan.FromMilliseconds(250);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(
                    ".",
                    _pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous
                );
                await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromMilliseconds(250);
                await RunConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromMilliseconds(
                    Math.Min(retryDelay.TotalMilliseconds * 2, 60_000)
                );
            }
            catch (UnauthorizedAccessException exception)
            {
                SupervisorLog.WriteError("Stream Deck status bridge access was denied.", exception);
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromMilliseconds(
                    Math.Min(retryDelay.TotalMilliseconds * 2, 60_000)
                );
            }
            catch (Exception exception)
            {
                SupervisorLog.WriteError("Stream Deck status bridge failed.", exception);
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromMilliseconds(
                    Math.Min(retryDelay.TotalMilliseconds * 2, 60_000)
                );
            }
        }
    }

    private async Task RunConnectionAsync(
        NamedPipeClientStream pipe,
        CancellationToken cancellationToken)
    {
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        Task reader = ReadCommandsAsync(pipe, connectionCancellation.Token);
        Task writer = WriteStatusesAsync(pipe, connectionCancellation.Token);
        await Task.WhenAny(reader, writer).ConfigureAwait(false);
        connectionCancellation.Cancel();
        await ObserveConnectionTaskAsync(reader).ConfigureAwait(false);
        await ObserveConnectionTaskAsync(writer).ConfigureAwait(false);
    }

    private async Task WriteStatusesAsync(
        NamedPipeClientStream pipe,
        CancellationToken cancellationToken)
    {
        StreamDeckStatusSnapshot lastSent = Volatile.Read(ref _snapshot);
        await WriteStatusAsync(pipe, lastSent, cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            await _statusChanged.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            while (_statusChanged.Reader.TryRead(out _))
            {
            }

            StreamDeckStatusSnapshot latest = Volatile.Read(ref _snapshot);
            if (latest == lastSent)
                continue;

            await WriteStatusAsync(pipe, latest, cancellationToken).ConfigureAwait(false);
            lastSent = latest;
        }
    }

    private static async Task WriteStatusAsync(
        NamedPipeClientStream pipe,
        StreamDeckStatusSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = ProtocolVersion,
            snapshot.State,
            snapshot.Title,
            snapshot.Tooltip,
            snapshot.Image,
            profiles = snapshot.LaunchProfiles
        }, JsonOptions);
        await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await pipe.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadCommandsAsync(
        NamedPipeClientStream pipe,
        CancellationToken cancellationToken)
    {
        var command = new StringBuilder(MaximumCommandLength);
        bool commandTooLong = false;
        byte[] buffer = new byte[128];

        while (!cancellationToken.IsCancellationRequested)
        {
            int read = await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return;

            for (int index = 0; index < read; index++)
            {
                char character = (char)buffer[index];
                if (character == '\n')
                {
                    if (!commandTooLong)
                        ProcessCommand(command.ToString().TrimEnd('\r'));

                    command.Clear();
                    commandTooLong = false;
                }
                else if (!commandTooLong)
                {
                    if (command.Length >= MaximumCommandLength)
                        commandTooLong = true;
                    else
                        command.Append(character);
                }
            }
        }
    }

    private void ProcessCommand(string command)
    {
        if (string.Equals(command, OpenConfigurationCommand, StringComparison.Ordinal))
        {
            DispatchCommand(ConfigurationRequested, "configuration request");
            return;
        }

        if (!command.StartsWith(LaunchProfileCommandPrefix, StringComparison.Ordinal))
            return;

        string encodedProfileId = command[LaunchProfileCommandPrefix.Length..];
        if (encodedProfileId.Length == 0)
            return;

        try
        {
            string profileId = Uri.UnescapeDataString(encodedProfileId);
            if (profileId.Length is 0 or > 128 ||
                profileId.Any(character => char.IsControl(character)))
            {
                return;
            }

            ProfileLaunchRequested?.Invoke(profileId);
        }
        catch (Exception exception)
        {
            SupervisorLog.WriteError(
                "The Stream Deck profile launch request could not be dispatched.",
                exception
            );
        }
    }

    private static void DispatchCommand(Action? handler, string description)
    {
        try
        {
            handler?.Invoke();
        }
        catch (Exception exception)
        {
            SupervisorLog.WriteError(
                $"The Stream Deck {description} could not be dispatched.",
                exception
            );
        }
    }

    private static async Task ObserveConnectionTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // The Stream Deck plugin disconnected; the client loop will await its next server.
        }
    }
}
