using AppSupervisor.Resources;
using AppSupervisor.Twitch;

namespace AppSupervisor.Tests;

/// <summary>Verifies one-shot actions and exact chat-mode restoration.</summary>
public sealed class TwitchResourceTests
{
    [Fact]
    public void FollowersOnly_ActivationAndDeactivation_RestoreOriginalSettings()
    {
        var client = new FakeTwitchClient
        {
            Settings = new TwitchChatSettings(false, true, 45, true, 12, false)
        };
        using var resource = new TwitchResource(new TwitchResourceConfig
        {
            Action = TwitchActionType.FollowersOnly,
            ModeEnabled = true,
            FollowerDurationMinutes = 10
        }, client);

        resource.Activate();
        resource.AdvanceLifecycle(DateTime.UtcNow);
        Assert.True(SpinWait.SpinUntil(resource.IsStarted, TimeSpan.FromSeconds(2)));
        TwitchChatSettingsUpdate applied = Assert.Single(client.Updates);
        Assert.True(applied.FollowerMode);
        Assert.Equal(10, applied.FollowerModeDuration);

        resource.Deactivate();
        resource.AdvanceLifecycle(DateTime.UtcNow);
        Assert.True(SpinWait.SpinUntil(() => !resource.DeactivationPending, TimeSpan.FromSeconds(2)));
        TwitchChatSettingsUpdate restored = client.Updates[1];
        Assert.True(restored.FollowerMode);
        Assert.Equal(45, restored.FollowerModeDuration);
    }

    [Fact]
    public void SlowMode_WhenOriginallyDisabled_RestoreDisablesWithoutDuration()
    {
        var original = new TwitchChatSettings(false, false, null, false, null, false);
        TwitchChatSettingsUpdate restored = TwitchResource.CreateRestoreUpdate(
            TwitchActionType.SlowMode,
            original
        );

        Assert.False(restored.SlowMode);
        Assert.Null(restored.SlowModeWaitTime);
    }

    [Fact]
    public void ChatMessage_RunsOnceAndHasNoDeactivationAction()
    {
        var client = new FakeTwitchClient();
        using var resource = new TwitchResource(new TwitchResourceConfig
        {
            Action = TwitchActionType.SendChatMessage,
            Message = "Hello twitchdevHype"
        }, client);

        resource.Activate();
        resource.AdvanceLifecycle(DateTime.UtcNow);
        Assert.True(SpinWait.SpinUntil(resource.IsStarted, TimeSpan.FromSeconds(2)));
        resource.Deactivate();
        resource.SuperviseDeactivation();
        resource.AdvanceLifecycle(DateTime.UtcNow);

        Assert.Equal(["Hello twitchdevHype"], client.Messages);
        Assert.Empty(client.Updates);
        Assert.False(resource.DeactivationPending);
    }

    private sealed class FakeTwitchClient : ITwitchApiClient
    {
        public TwitchChatSettings Settings { get; set; } =
            new(false, false, null, false, null, false);
        public List<TwitchChatSettingsUpdate> Updates { get; } = [];
        public List<string> Messages { get; } = [];
        public List<int> Commercials { get; } = [];

        public Task<TwitchChatSettings> GetChatSettingsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Settings);

        public Task UpdateChatSettingsAsync(TwitchChatSettingsUpdate update, CancellationToken cancellationToken)
        {
            Updates.Add(update);
            return Task.CompletedTask;
        }

        public Task SendChatMessageAsync(string message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }

        public Task RunCommercialAsync(int lengthSeconds, CancellationToken cancellationToken)
        {
            Commercials.Add(lengthSeconds);
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }
}
