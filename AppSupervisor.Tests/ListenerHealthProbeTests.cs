using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AppSupervisor.Health;

namespace AppSupervisor.Tests;

/// <summary>
/// Verifies listener probing against real Windows TCP ownership data.
/// </summary>
public sealed class ListenerHealthProbeTests
{
    /// <summary>Confirms address is irrelevant while port and owning helper PID must match.</summary>
    [Fact]
    public async Task CheckAsync_OwnedTcpListener_ReturnsHealthy()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var probe = new ListenerHealthProbe(ListenerProtocol.Tcp, port);
            var ownerProcessIds = new HashSet<int>
            {
                Environment.ProcessId
            };

            HealthProbeResult result = await probe.CheckAsync(
                ownerProcessIds,
                CancellationToken.None
            );

            Assert.True(result.Healthy, result.Detail);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Confirms a real listener owned by a different PID does not satisfy the helper check.</summary>
    [Fact]
    public async Task CheckAsync_WrongOwnerPid_ReturnsUnhealthy()
    {
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();

        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var probe = new ListenerHealthProbe(ListenerProtocol.Tcp, port);
            var wrongOwner = new HashSet<int>
            {
                int.MaxValue
            };

            HealthProbeResult result = await probe.CheckAsync(
                wrongOwner,
                CancellationToken.None
            );

            Assert.False(result.Healthy);
        }
        finally
        {
            listener.Stop();
        }
    }
}
