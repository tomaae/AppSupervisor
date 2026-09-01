using AppSupervisor.Core;
using AppSupervisor.Triggers;

namespace AppSupervisor.Tests;

/// <summary>Verifies that profile prerequisites suppress monitored-trigger polling.</summary>
public sealed class ProfileDependencyTriggerTests
{
    [Fact]
    public void IsActive_DependencyNotRunning_DoesNotPollInnerTrigger()
    {
        var inner = new CountingTrigger { Active = true };
        var trigger = new ProfileDependencyTrigger(inner, () => false);

        Assert.False(trigger.IsActive());
        Assert.Equal(0, inner.Polls);
    }

    [Fact]
    public void IsActive_DependencyRunning_ReturnsInnerTriggerState()
    {
        var inner = new CountingTrigger { Active = true };
        bool dependencyRunning = true;
        var trigger = new ProfileDependencyTrigger(inner, () => dependencyRunning);

        Assert.True(trigger.IsActive());
        Assert.Equal(1, inner.Polls);

        dependencyRunning = false;
        Assert.False(trigger.IsActive());
        Assert.Equal(1, inner.Polls);
    }

    private sealed class CountingTrigger : ITrigger
    {
        public bool Active { get; set; }

        public int Polls { get; private set; }

        public bool IsActive()
        {
            Polls++;
            return Active;
        }
    }
}
