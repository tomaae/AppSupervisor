using AppSupervisor.Health;

namespace AppSupervisor.Tests;

/// <summary>
/// Verifies address-independent shared listener snapshots and probe ownership matching.
/// </summary>
public sealed class ListenerSnapshotTests
{
    /// <summary>Confirms a shared UDP endpoint satisfies only the matching owner and local port.</summary>
    [Fact]
    public async Task CheckAsync_SharedUdpSnapshot_MatchesPortAndOwner()
    {
        const int processId = 1234;
        var snapshot = new ListenerSnapshot(
        [
            new ListenerEndpoint(ListenerProtocol.Udp, 9000, processId)
        ]);
        var provider = new FixedListenerSnapshotProvider(snapshot);
        using var probe = new ListenerHealthProbe(
            ListenerProtocol.Udp,
            9000,
            provider
        );

        HealthProbeResult result = await probe.CheckAsync(
            new HashSet<int> { processId },
            CancellationToken.None
        );

        Assert.True(result.Healthy, result.Detail);
        Assert.Equal(1, provider.ReadCount);
    }

    /// <summary>Returns one fixed immutable snapshot and records probe consumption.</summary>
    private sealed class FixedListenerSnapshotProvider : IListenerSnapshotProvider
    {
        private readonly ListenerSnapshot _snapshot;

        /// <summary>Creates a provider for one predetermined snapshot.</summary>
        /// <param name="snapshot">The snapshot returned to probes.</param>
        public FixedListenerSnapshotProvider(ListenerSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        /// <summary>Gets the number of snapshot reads.</summary>
        public int ReadCount { get; private set; }

        /// <summary>Returns the fixed snapshot and increments the read count.</summary>
        /// <returns>The configured immutable snapshot.</returns>
        public ListenerSnapshot GetSnapshot()
        {
            ReadCount++;
            return _snapshot;
        }
    }
}
