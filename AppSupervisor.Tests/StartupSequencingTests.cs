using AppSupervisor.Core;
using AppSupervisor.Notifications;

namespace AppSupervisor.Tests;

/// <summary>Verifies ordered, nonblocking helper application and service startup.</summary>
public sealed class StartupSequencingTests
{
    /// <summary>Confirms the profile-level startup delay postpones its first resource.</summary>
    [Fact]
    public void AdvanceStartup_ProfileStartupDelay_DelaysFirstResource()
    {
        var trigger = new FakeTrigger { Active = true };
        var resource = new FakeResource();
        using var profile = new SupervisorProfile(
            "Delayed profile",
            "trigger.exe",
            trigger,
            [new ManagedResourceStartup(resource, "resource", 0, "")],
            TimeSpan.Zero,
            TimeSpan.FromMinutes(1)
        );

        profile.Update();
        DateTime afterActivationUtc = DateTime.UtcNow;

        Assert.Equal(0, resource.ActivateCalls);
        profile.AdvanceStartup(afterActivationUtc.AddSeconds(59));
        Assert.Equal(0, resource.ActivateCalls);

        profile.AdvanceStartup(afterActivationUtc.AddSeconds(61));

        Assert.Equal(1, resource.ActivateCalls);
    }

    /// <summary>Confirms a wait delays only later resources in the same profile.</summary>
    [Fact]
    public void AdvanceStartup_WaitAfterStartup_DelaysFollowingResources()
    {
        var trigger = new FakeTrigger { Active = true };
        var first = new FakeResource();
        var second = new FakeResource();
        var third = new FakeResource();
        using var profile = CreateProfile(
            trigger,
            new ManagedResourceStartup(first, "first", 60_000, ""),
            new ManagedResourceStartup(second, "second", 0, ""),
            new ManagedResourceStartup(third, "third", 0, "")
        );

        profile.Update();
        DateTime afterActivationUtc = DateTime.UtcNow;

        Assert.Equal(1, first.ActivateCalls);
        Assert.Equal(0, second.ActivateCalls);
        profile.AdvanceStartup(afterActivationUtc.AddSeconds(59));
        Assert.Equal(0, second.ActivateCalls);

        profile.AdvanceStartup(afterActivationUtc.AddSeconds(61));

        Assert.Equal(1, second.ActivateCalls);
        Assert.Equal(1, third.ActivateCalls);
    }

    /// <summary>Confirms a dependency waits until its earlier resource reports a started state.</summary>
    [Fact]
    public void AdvanceStartup_DependencyNotStarted_WaitsForReadiness()
    {
        var trigger = new FakeTrigger { Active = true };
        var dependency = new FakeResource { Started = false };
        var dependent = new FakeResource();
        using var profile = CreateProfile(
            trigger,
            new ManagedResourceStartup(dependency, "dependency", 0, ""),
            new ManagedResourceStartup(dependent, "dependent", 0, "dependency")
        );

        profile.Update();
        profile.AdvanceStartup(DateTime.UtcNow.AddHours(1));

        Assert.Equal(1, dependency.ActivateCalls);
        Assert.Equal(0, dependent.ActivateCalls);

        dependency.Started = true;
        profile.AdvanceStartup(DateTime.UtcNow.AddHours(1));

        Assert.Equal(1, dependent.ActivateCalls);
    }

    /// <summary>Confirms one profile's wait cannot prevent another profile from activating.</summary>
    [Fact]
    public void Update_SeparateProfiles_AdvanceIndependently()
    {
        var waitingFirst = new FakeResource();
        var waitingSecond = new FakeResource();
        var independent = new FakeResource();
        using var waitingProfile = CreateProfile(
            new FakeTrigger { Active = true },
            new ManagedResourceStartup(waitingFirst, "first", 60_000, ""),
            new ManagedResourceStartup(waitingSecond, "second", 0, "")
        );
        using var independentProfile = CreateProfile(
            new FakeTrigger { Active = true },
            new ManagedResourceStartup(independent, "independent", 0, "")
        );

        waitingProfile.Update();
        independentProfile.Update();

        Assert.Equal(0, waitingSecond.ActivateCalls);
        Assert.Equal(1, independent.ActivateCalls);
    }

    /// <summary>Confirms trigger loss cancels unissued entries and closes only reached resources.</summary>
    [Fact]
    public void Update_TriggerEndsDuringWait_CancelsRemainingStartup()
    {
        var trigger = new FakeTrigger { Active = true };
        var first = new FakeResource();
        var second = new FakeResource();
        using var profile = CreateProfile(
            trigger,
            new ManagedResourceStartup(first, "first", 60_000, ""),
            new ManagedResourceStartup(second, "second", 0, "")
        );

        profile.Update();
        trigger.Active = false;
        profile.Update();
        profile.Update();
        profile.AdvanceStartup(DateTime.MaxValue);

        Assert.Equal(1, first.DeactivateCalls);
        Assert.Equal(0, second.ActivateCalls);
        Assert.Equal(0, second.DeactivateCalls);
    }

    /// <summary>Creates one sequenced profile for deterministic tests.</summary>
    /// <param name="trigger">The mutable profile trigger.</param>
    /// <param name="resources">Ordered startup entries.</param>
    /// <returns>A profile with an immediate close timeout.</returns>
    private static SupervisorProfile CreateProfile(
        FakeTrigger trigger,
        params ManagedResourceStartup[] resources)
    {
        return new SupervisorProfile(
            "Sequencing test",
            "trigger.exe",
            trigger,
            resources,
            TimeSpan.Zero
        );
    }

    /// <summary>Supplies a mutable activation state.</summary>
    private sealed class FakeTrigger : ITrigger
    {
        /// <summary>Gets or sets whether the profile trigger is active.</summary>
        public bool Active { get; set; }

        /// <summary>Returns the configured activation state.</summary>
        /// <returns>The current value of <see cref="Active"/>.</returns>
        public bool IsActive() => Active;
    }

    /// <summary>Records resource lifecycle calls and exposes mutable readiness.</summary>
    private sealed class FakeResource : IManagedResource, IManagedResourceReadiness
    {
        /// <summary>Provides a no-op error event for the test resource contract.</summary>
        public event Action<IManagedResource, string>? ErrorOccurred
        {
            add { }
            remove { }
        }

        /// <summary>Gets the test resource's display name.</summary>
        public string DisplayName => "Fake resource";

        /// <summary>Gets the empty notification target collection.</summary>
        public IReadOnlyList<NotificationTarget> NotificationTargets => [];

        /// <summary>Gets the number of activation requests.</summary>
        public int ActivateCalls { get; private set; }

        /// <summary>Gets the number of deactivation requests.</summary>
        public int DeactivateCalls { get; private set; }

        /// <summary>Gets or sets whether the resource reports itself started.</summary>
        public bool Started { get; set; } = true;

        /// <summary>Records one activation request.</summary>
        public void Activate() => ActivateCalls++;

        /// <summary>Returns the test-controlled started state.</summary>
        /// <returns>The current value of <see cref="Started"/>.</returns>
        public bool IsStarted() => Started;

        /// <summary>Returns no supervision transition.</summary>
        /// <returns><see cref="ManagedResourceUpdate.None"/>.</returns>
        public ManagedResourceUpdate Supervise() => ManagedResourceUpdate.None;

        /// <summary>Performs no pending recovery work.</summary>
        public void CancelPendingRecovery() { }

        /// <summary>Performs no monitoring suspension work.</summary>
        public void SuspendMonitoring() { }

        /// <summary>Records one deactivation request.</summary>
        public void Deactivate() => DeactivateCalls++;

        /// <summary>Performs no deactivation supervision work.</summary>
        public void SuperviseDeactivation() { }

        /// <summary>Releases no external test resources.</summary>
        public void Dispose() { }
    }
}
