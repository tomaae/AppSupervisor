using AppSupervisor.Twitch;

namespace AppSupervisor.ConfigurationUI;

/// <summary>Runs the shared browser-based Twitch device authorization without extra UI steps.</summary>
internal static class TwitchConnectionFlow
{
    public static async Task<TwitchAuthorizationStatus> ConnectAsync(
        Action<string> updateStatus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updateStatus);

        updateStatus("Opening Twitch authorization...");
        using var authorization = new TwitchAuthorizationService(
            new TwitchIntegrationConfig()
        );
        TwitchDeviceAuthorization device = await authorization.BeginConnectAsync(cancellationToken);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            device.VerificationUri.AbsoluteUri
        )
        {
            UseShellExecute = true
        });
        updateStatus(
            $"Waiting for Twitch authorization (code {device.UserCode} if requested)..."
        );
        return await authorization.CompleteConnectAsync(device, cancellationToken);
    }
}
