namespace AppSupervisor.Twitch;

internal interface ITwitchApiClient : IDisposable
{
    Task<TwitchChatSettings> GetChatSettingsAsync(CancellationToken cancellationToken);
    Task UpdateChatSettingsAsync(TwitchChatSettingsUpdate update, CancellationToken cancellationToken);
    Task SendChatMessageAsync(string message, CancellationToken cancellationToken);
    Task RunCommercialAsync(int lengthSeconds, CancellationToken cancellationToken);
}

internal sealed record TwitchChatSettings(
    bool EmoteMode,
    bool FollowerMode,
    int? FollowerModeDuration,
    bool SlowMode,
    int? SlowModeWaitTime,
    bool SubscriberMode);

internal sealed class TwitchChatSettingsUpdate
{
    public bool? EmoteMode { get; init; }
    public bool? FollowerMode { get; init; }
    public int? FollowerModeDuration { get; init; }
    public bool? SlowMode { get; init; }
    public int? SlowModeWaitTime { get; init; }
    public bool? SubscriberMode { get; init; }
}
