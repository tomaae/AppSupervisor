using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace AppSupervisor.StreamDeck;

/// <summary>
/// Pushes deduplicated tray presentations to the local Stream Deck plugin and receives its
/// configuration-open command without polling or exposing a network listener.
/// </summary>
internal sealed class StreamDeckStatusServer : IAsyncDisposable
{
    public const string PipeName = "AppSupervisor.StreamDeck.v1";
    public const int ProtocolVersion = 1;
    public const string OpenConfigurationCommand = "openConfiguration";
    private const int MaximumCommandLength = 64;

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
    private readonly Task _serverTask;
    private StreamDeckStatusSnapshot _snapshot;
    private int _disposed;

    public StreamDeckStatusServer(
        StreamDeckStatusSnapshot initialSnapshot,
        string pipeName = PipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _snapshot = initialSnapshot;
        _pipeName = pipeName;
        _serverTask = Task.Run(() => RunServerAsync(_shutdown.Token));
    }

    /// <summary>Raised when the user presses any visible AppSupervisor Stream Deck action.</summary>
    public event Action? ConfigurationRequested;

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
            await _serverTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private async Task RunServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await AcceptOneClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                SupervisorLog.WriteError("Stream Deck status bridge failed.", exception);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task AcceptOneClientAsync(CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
        );
        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

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
        NamedPipeServerStream pipe,
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
        NamedPipeServerStream pipe,
        StreamDeckStatusSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = ProtocolVersion,
            snapshot.State,
            snapshot.Title,
            snapshot.Tooltip,
            snapshot.Image
        }, JsonOptions);
        await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await pipe.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadCommandsAsync(
        NamedPipeServerStream pipe,
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
        if (!string.Equals(command, OpenConfigurationCommand, StringComparison.Ordinal))
            return;

        try
        {
            ConfigurationRequested?.Invoke();
        }
        catch (Exception exception)
        {
            SupervisorLog.WriteError(
                "The Stream Deck configuration request could not be dispatched.",
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
            // The Stream Deck plugin disconnected; the accept loop will await its next connection.
        }
    }
}
