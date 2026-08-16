namespace AppSupervisor.Twitch;

internal sealed class TwitchStoredAuthorization
{
    public string ClientId { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Login { get; set; } = "";
    public DateTimeOffset ExpiresAtUtc { get; set; }
}
