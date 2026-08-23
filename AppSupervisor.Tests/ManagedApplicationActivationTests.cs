using AppSupervisor.Resources;
using AppSupervisor.Core;

namespace AppSupervisor.Tests;

/// <summary>Verifies profile activation behavior for helpers that are already available.</summary>
public sealed class ManagedApplicationActivationTests
{
    [Fact]
    public void Activate_OneExistingInstance_StartsConfiguredMacro()
    {
        using var application = new ManagedApplication(
            new ManagedApplicationConfig
            {
                Path = "helper.exe",
                StartupMacros =
                [
                    new StartupMacroActionConfig
                    {
                        Type = StartupMacroActionType.Delay,
                        DelayMilliseconds = 100
                    }
                ]
            },
            TimeSpan.Zero,
            shouldRemainRunning: null,
            processIdProvider: () => new HashSet<int> { 42 }
        );

        application.Activate();

        Assert.True(application.ApiMacroPending);
        Assert.Equal(
            ConfigurationResourceRuntimeStatus.Starting,
            application.CachedRuntimeStatus
        );
    }

    [Fact]
    public void Activate_ExistingInstanceWithoutMacros_HasNoMacroWork()
    {
        using var application = new ManagedApplication(
            new ManagedApplicationConfig { Path = "helper.exe" },
            TimeSpan.Zero,
            shouldRemainRunning: null,
            processIdProvider: () => new HashSet<int> { 42 }
        );

        application.Activate();

        Assert.False(application.ApiMacroPending);
    }

    [Fact]
    public void Supervise_ProcessAppearsAfterReleasedStartTransition_StartsConfiguredMacro()
    {
        bool running = false;
        using var application = new ManagedApplication(
            new ManagedApplicationConfig
            {
                Path = "slow-helper.exe",
                StartupMacros =
                [
                    new StartupMacroActionConfig
                    {
                        Type = StartupMacroActionType.Delay,
                        DelayMilliseconds = 100
                    }
                ]
            },
            TimeSpan.Zero,
            shouldRemainRunning: null,
            processIdProvider: () => running ? new HashSet<int> { 42 } : []
        );

        application.Activate();
        Assert.False(application.ApiMacroPending);

        ProcessPathSnapshot.CompleteTransition(
            application.RuntimePath,
            application,
            succeeded: false
        );
        running = true;

        application.Supervise();

        Assert.True(application.ApiMacroPending);
    }

    [Fact]
    public void CachedRuntimeStatus_ObservationsAndCloseTransition_NeverDiscoverOnRead()
    {
        bool running = false;
        int observations = 0;
        using var application = new ManagedApplication(
            new ManagedApplicationConfig { Path = "helper.exe" },
            TimeSpan.Zero,
            shouldRemainRunning: null,
            processIdProvider: () =>
            {
                observations++;
                return running ? new HashSet<int> { 42 } : [];
            }
        );

        Assert.Equal(ConfigurationResourceRuntimeStatus.Unknown, application.CachedRuntimeStatus);
        Assert.Equal(0, observations);

        Assert.False(application.IsRunning());
        Assert.Equal(
            ConfigurationResourceRuntimeStatus.NotRunning,
            application.CachedRuntimeStatus
        );
        Assert.Equal(1, observations);

        running = true;
        Assert.True(application.IsRunning());
        Assert.Equal(ConfigurationResourceRuntimeStatus.Running, application.CachedRuntimeStatus);
        Assert.Equal(2, observations);

        ProcessPathSnapshot.RequestTransition(
            "helper.exe",
            application,
            ProcessLifecycleTransitionKind.Close
        );
        Assert.Equal(
            ConfigurationResourceRuntimeStatus.Stopping,
            application.CachedRuntimeStatus
        );
        Assert.Equal(2, observations);
    }
}
