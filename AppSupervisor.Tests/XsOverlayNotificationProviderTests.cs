using AppSupervisor.Notifications;

namespace AppSupervisor.Tests;

/// <summary>
/// Verifies XSOverlay connection settings independently of a live VR session.
/// </summary>
public sealed class XsOverlayNotificationProviderTests
{
    /// <summary>
    /// Confirms an inaccessible process path still attempts the documented local WebSocket port.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ResolveWebSocketPort_InaccessibleExecutablePath_UsesDefault(string? executablePath)
    {
        Assert.Equal(42070, XsOverlayNotificationProvider.ResolveWebSocketPort(executablePath));
    }

    /// <summary>
    /// Confirms delivery bypasses slow localhost name and proxy discovery while retaining the client token.
    /// </summary>
    [Fact]
    public void CreateWebSocketUri_UsesDirectLoopbackAndClientToken()
    {
        Uri uri = XsOverlayNotificationProvider.CreateWebSocketUri(42070);

        Assert.Equal("127.0.0.1", uri.Host);
        Assert.Equal(42070, uri.Port);
        Assert.Equal("?client=AppSupervisor", uri.Query);
    }

    /// <summary>Confirms shutdown prevents later delivery without attempting another connection.</summary>
    [Fact]
    public async Task SendAsync_AfterDispose_ReturnsFalse()
    {
        var provider = new XsOverlayNotificationProvider();
        provider.Dispose();

        bool delivered = await provider.SendAsync(
            new SupervisorNotification(
                NotificationSeverity.Information,
                "Test",
                "Test",
                [NotificationTarget.XsOverlay]
            ),
            CancellationToken.None
        );

        Assert.False(delivered);
    }
}
