using System.Net;
using System.Net.Sockets;
using System.Text;
using AppSupervisor.SupervisorApi;

namespace AppSupervisor.Tests;

/// <summary>Exercises request deadlines and connection admission using isolated loopback ports.</summary>
public sealed class SupervisorApiServerTests
{
    [Fact]
    public async Task IncompleteRequest_DeadlineClosesClient_AndServerRemainsAvailable()
    {
        using var server = new SupervisorApiServer(port: 0,
            requestTimeout: TimeSpan.FromMilliseconds(250));
        server.ApplyConfiguration(new SupervisorApiConfig { Enabled = true });
        using var stalled = await ConnectAsync(server);
        await stalled.GetStream().WriteAsync("GET / HTTP/1.1\r\n"u8.ToArray());

        int read = await stalled.GetStream().ReadAsync(new byte[1]).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, read);
        await WaitForClientsAsync(server, 0);

        using var healthy = await ConnectAsync(server);
        Assert.Contains("HTTP/1.1 200 OK", await GetResponseAsync(healthy));
    }

    [Fact]
    public async Task ConcurrentClientLimit_QueuesNextRequestUntilSlotIsReleased()
    {
        using var server = new SupervisorApiServer(port: 0,
            requestTimeout: TimeSpan.FromSeconds(10), maximumConcurrentClients: 1);
        server.ApplyConfiguration(new SupervisorApiConfig { Enabled = true });
        using var stalled = await ConnectAsync(server);
        await WaitForClientsAsync(server, 1);
        using var queued = await ConnectAsync(server);
        Task<string> response = GetResponseAsync(queued);

        await Task.Delay(100);
        Assert.Equal(1, server.ActiveClientCount);
        Assert.False(response.IsCompleted);

        stalled.Dispose();
        Assert.Contains("HTTP/1.1 200 OK", await response);
        await WaitForClientsAsync(server, 0);
    }

    [Fact]
    public async Task Disable_CancelsStalledClients_AndAllowsRestart()
    {
        using var server = new SupervisorApiServer(port: 0,
            requestTimeout: TimeSpan.FromSeconds(30));
        server.ApplyConfiguration(new SupervisorApiConfig { Enabled = true });
        using var stalled = await ConnectAsync(server);
        await WaitForClientsAsync(server, 1);
        server.ApplyConfiguration(new SupervisorApiConfig { Enabled = false });

        Assert.Equal(0, await stalled.GetStream().ReadAsync(new byte[1]).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5)));
        await WaitForClientsAsync(server, 0);

        server.ApplyConfiguration(new SupervisorApiConfig { Enabled = true });
        using var healthy = await ConnectAsync(server);
        Assert.Contains("HTTP/1.1 200 OK", await GetResponseAsync(healthy));
    }

    private static async Task<TcpClient> ConnectAsync(SupervisorApiServer server)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, server.ListeningPort)
                .WaitAsync(TimeSpan.FromSeconds(5));
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<string> GetResponseAsync(TcpClient client)
    {
        await client.GetStream().WriteAsync("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n"u8.ToArray());
        using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
        return await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitForClientsAsync(SupervisorApiServer server, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.ActiveClientCount != count)
            await Task.Delay(10, timeout.Token);
    }
}
