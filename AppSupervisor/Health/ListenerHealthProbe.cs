namespace AppSupervisor.Health;

/// <summary>
/// Checks a shared native Windows listener snapshot for a port owned by the managed helper process.
/// </summary>
public sealed class ListenerHealthProbe : IHealthProbe
{
    private readonly ListenerProtocol _protocol;
    private readonly int _port;
    private readonly IListenerSnapshotProvider _snapshotProvider;

    /// <summary>Creates a listener probe using the process-wide shared native snapshot provider.</summary>
    /// <param name="protocol">The TCP or UDP transport to inspect.</param>
    /// <param name="port">The local port that must be listening or bound.</param>
    public ListenerHealthProbe(ListenerProtocol protocol, int port)
        : this(protocol, port, WindowsListenerSnapshotProvider.Instance)
    {
    }

    /// <summary>Creates a listener probe with an explicitly supplied snapshot provider.</summary>
    /// <param name="protocol">The TCP or UDP transport to inspect.</param>
    /// <param name="port">The local port that must be listening or bound.</param>
    /// <param name="snapshotProvider">The native snapshot source.</param>
    internal ListenerHealthProbe(
        ListenerProtocol protocol,
        int port,
        IListenerSnapshotProvider snapshotProvider)
    {
        _protocol = protocol;
        _port = port;
        _snapshotProvider = snapshotProvider;
    }

    /// <summary>Checks the shared snapshot for a matching port and owner process.</summary>
    /// <param name="ownerProcessIds">The currently running helper process identifiers.</param>
    /// <param name="cancellationToken">Cancels the snapshot lookup.</param>
    /// <returns>A completed healthy or unhealthy listener result.</returns>
    public Task<HealthProbeResult> CheckAsync(
        IReadOnlySet<int> ownerProcessIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ownerProcessIds.Count == 0)
        {
            return Task.FromResult(
                HealthProbeResult.Failure("The helper process is not running.")
            );
        }

        ListenerSnapshot snapshot = _snapshotProvider.GetSnapshot();
        string protocolName = _protocol.ToString().ToUpperInvariant();

        return Task.FromResult(snapshot.Contains(_protocol, _port, ownerProcessIds)
            ? HealthProbeResult.Success(
                $"{protocolName} port {_port} is owned by the helper."
            )
            : HealthProbeResult.Failure(
                $"No {protocolName} listener on port {_port} is owned by the helper process."
            ));
    }

    /// <summary>Clears retained probe state; native listener probes retain no per-check state.</summary>
    public void Reset()
    {
    }

    /// <summary>Releases probe resources; the shared provider is process-owned.</summary>
    public void Dispose()
    {
    }
}
