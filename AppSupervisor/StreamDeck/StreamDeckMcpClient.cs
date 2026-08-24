using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace AppSupervisor.StreamDeck;

/// <summary>
/// Owns one lazily started stdio session with Elgato's official MCP server. Calls are serialized,
/// so the idle process uses no polling and no per-resource background workers.
/// </summary>
internal sealed class StreamDeckMcpClient : IStreamDeckMcpClient, IAsyncDisposable
{
    private static readonly Lazy<StreamDeckMcpClient> SharedClient = new(() => new());
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(35);

    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly ConcurrentQueue<string> _standardError = new();
    private Process? _process;
    private StreamDeckMcpProtocolClient? _protocol;
    private bool _disposed;

    public static IStreamDeckMcpClient Shared => SharedClient.Value;

    public async Task<IReadOnlyList<StreamDeckMcpAction>> LoadActionsAsync(
        CancellationToken cancellationToken) =>
        await ExecuteAsync(
            (protocol, token) => protocol.LoadActionsAsync(token),
            cancellationToken
        ).ConfigureAwait(false);

    public async Task ExecuteActionAsync(
        StreamDeckResourceConfig configuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.ToolName);
        await ExecuteAsync(
            async (protocol, token) =>
            {
                await protocol.ExecuteActionAsync(configuration.ToolName, token)
                    .ConfigureAwait(false);
                return true;
            },
            cancellationToken
        ).ConfigureAwait(false);
    }

    public static async ValueTask ShutdownSharedAsync()
    {
        if (SharedClient.IsValueCreated)
            await SharedClient.Value.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _requestLock.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_disposed)
                return;

            _disposed = true;
            await ResetProcessAsync().ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
            _requestLock.Dispose();
        }
    }

    private async Task<T> ExecuteAsync<T>(
        Func<StreamDeckMcpProtocolClient, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            try
            {
                StreamDeckMcpProtocolClient protocol = await EnsureStartedAsync(timeout.Token)
                    .ConfigureAwait(false);
                return await action(protocol, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                await ResetProcessAsync().ConfigureAwait(false);
                throw new TimeoutException("Elgato MCP Server did not respond within 35 seconds.");
            }
            catch
            {
                await ResetProcessAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task<StreamDeckMcpProtocolClient> EnsureStartedAsync(
        CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } && _protocol is not null)
            return _protocol;

        await ResetProcessAsync().ConfigureAwait(false);
        string commandPath = FindServerCommand();
        string commandInterpreter = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var startInfo = new ProcessStartInfo
        {
            FileName = commandInterpreter,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(commandPath) ?? AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(commandPath);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.ErrorDataReceived += ProcessErrorDataReceived;

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Elgato MCP Server could not be started.");

            process.BeginErrorReadLine();
            var protocol = new StreamDeckMcpProtocolClient(
                process.StandardOutput,
                process.StandardInput
            );
            _process = process;
            _protocol = protocol;
            await protocol.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return protocol;
        }
        catch (Exception exception)
        {
            _process = null;
            _protocol = null;
            process.ErrorDataReceived -= ProcessErrorDataReceived;

            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            process.Dispose();
            throw new InvalidOperationException(WithServerErrors(exception.Message), exception);
        }
    }

    private static string FindServerCommand()
    {
        var candidates = new List<string>();
        string applicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData
        );

        if (applicationData.Length > 0)
            candidates.Add(Path.Combine(applicationData, "npm", "elgato-mcp-server.cmd"));

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (string directory in path.Split(Path.PathSeparator))
            {
                string trimmed = directory.Trim().Trim('"');
                if (trimmed.Length > 0)
                    candidates.Add(Path.Combine(trimmed, "elgato-mcp-server.cmd"));
            }
        }

        string? found = candidates.FirstOrDefault(File.Exists);
        return found ?? throw new FileNotFoundException(
            "Elgato MCP Server is not installed. Enable MCP Deck in Stream Deck Preferences, " +
            "then run 'npm install -g @elgato/mcp-server'."
        );
    }

    private async Task ResetProcessAsync()
    {
        Process? process = _process;
        _process = null;
        _protocol = null;

        if (process is null)
            return;

        process.ErrorDataReceived -= ProcessErrorDataReceived;

        try
        {
            process.StandardInput.Close();

            if (!process.HasExited)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private void ProcessErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
            return;

        _standardError.Enqueue(e.Data.Trim());
        while (_standardError.Count > 8)
            _standardError.TryDequeue(out _);
    }

    private string WithServerErrors(string message)
    {
        string[] errors = _standardError.ToArray();
        return errors.Length == 0 ? message : $"{message} {string.Join(" ", errors)}";
    }
}
