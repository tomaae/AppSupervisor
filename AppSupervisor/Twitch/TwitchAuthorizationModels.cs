namespace AppSupervisor.Twitch;

internal sealed record TwitchDeviceAuthorization(
    string DeviceCode,
    string UserCode,
    Uri VerificationUri,
    DateTimeOffset ExpiresAtUtc,
    TimeSpan PollInterval);

internal sealed record TwitchAuthorizationStatus(bool Connected, string Login)
{
    public static TwitchAuthorizationStatus Disconnected { get; } = new(false, "");
}

internal sealed record TwitchAccess(string ClientId, string AccessToken, string UserId, string Login);
