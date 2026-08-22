using AppSupervisor.Resources;

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
}
