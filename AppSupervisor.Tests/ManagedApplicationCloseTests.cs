using AppSupervisor.Resources;

namespace AppSupervisor.Tests;

/// <summary>Verifies graceful window targeting and explicit tray termination labels.</summary>
public sealed class ManagedApplicationCloseTests
{
    /// <summary>Confirms the opt-in persistent helper bypasses normal profile deactivation.</summary>
    [Fact]
    public void Deactivate_LeaveRunningEnabled_SkipsSharedCloseGuardAndProcessLookup()
    {
        int closeGuardCalls = 0;
        using var application = new ManagedApplication(
            new ManagedApplicationConfig
            {
                Path = "\0invalid",
                LeaveRunningAfterProfileStops = true
            },
            TimeSpan.Zero,
            () =>
            {
                closeGuardCalls++;
                return false;
            }
        );

        application.Deactivate();

        Assert.Equal(0, closeGuardCalls);
        Assert.False(((IManagedApplicationLifecycle)application).CloseOperationPending);
    }

    /// <summary>Confirms only visible owned windows receive the initial WM_CLOSE request.</summary>
    /// <param name="visible">Whether the candidate window is visible.</param>
    /// <param name="expected">Whether the candidate should receive WM_CLOSE.</param>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void IsOwnedCloseTarget_Visibility_SelectsExpectedWindow(
        bool visible,
        bool expected)
    {
        bool result = ManagedApplication.IsOwnedCloseTarget(
            targetProcessId: 42,
            windowThreadId: 7,
            windowProcessId: 42,
            isVisible: visible
        );

        Assert.Equal(expected, result);
    }

    /// <summary>Confirms invalid or foreign windows are never sent a helper close request.</summary>
    /// <param name="threadId">The candidate window thread identifier.</param>
    /// <param name="windowProcessId">The candidate owner process identifier.</param>
    [Theory]
    [InlineData(0u, 42u)]
    [InlineData(7u, 99u)]
    public void IsOwnedCloseTarget_InvalidOrForeignWindow_ReturnsFalse(
        uint threadId,
        uint windowProcessId)
    {
        bool result = ManagedApplication.IsOwnedCloseTarget(
            targetProcessId: 42,
            windowThreadId: threadId,
            windowProcessId,
            isVisible: true
        );

        Assert.False(result);
    }
    /// <summary>Confirms tray fallback accepts only explicit Quit or Exit labels.</summary>
    /// <param name="name">The accessible command name.</param>
    /// <param name="expected">Whether the command safely represents application termination.</param>
    [Theory]
    [InlineData("Quit", true)]
    [InlineData("exit", true)]
    [InlineData("&Exit", true)]
    [InlineData("Quit.", true)]
    [InlineData("Restart", false)]
    [InlineData("Exit settings", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsExitCommandName_Label_ReturnsExpectedResult(
        string? name,
        bool expected)
    {
        bool result = TrayExitCloser.IsExitCommandName(name);

        Assert.Equal(expected, result);
    }
}
