using AppSupervisor.Notifications;

namespace AppSupervisor.Tests;

public sealed class ActiveNotificationDeduplicatorTests
{
    [Fact]
    public void TryActivate_RepeatedActiveIdentity_IsPublishedOnce()
    {
        var deduplicator = new ActiveNotificationDeduplicator<string>();

        Assert.True(deduplicator.TryActivate("profile|resource|message"));
        Assert.False(deduplicator.TryActivate("profile|resource|message"));
        Assert.True(deduplicator.TryActivate("profile|resource|different message"));
    }

    [Fact]
    public void ClearWhere_Recovery_AllowsTheSameFailureToBePublishedAgain()
    {
        var deduplicator = new ActiveNotificationDeduplicator<string>();
        deduplicator.TryActivate("resource-a|message");
        deduplicator.TryActivate("resource-b|message");

        deduplicator.ClearWhere(identity => identity.StartsWith("resource-a|"));

        Assert.True(deduplicator.TryActivate("resource-a|message"));
        Assert.False(deduplicator.TryActivate("resource-b|message"));
    }
}
