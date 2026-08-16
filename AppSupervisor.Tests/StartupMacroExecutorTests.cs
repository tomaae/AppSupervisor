using AppSupervisor.Resources;
using System.Runtime.InteropServices;

namespace AppSupervisor.Tests;

/// <summary>Verifies ordered macro timing, continuation, and completion without native window mutation.</summary>
public sealed class StartupMacroExecutorTests
{
    [Fact]
    public void NativeInputLayout_MatchesWindowsAbi()
    {
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    [Fact]
    public void Advance_DelayThenTwoHotkeys_ExecutesInOrder()
    {
        List<StartupMacroActionConfig> actions =
        [
            new() { Type = StartupMacroActionType.Delay, DelayMilliseconds = 2_000 },
            new() { Type = StartupMacroActionType.Hotkey, Keys = ["ControlKey", "F5"] },
            new() { Type = StartupMacroActionType.Hotkey, Keys = ["ControlKey", "F6"] }
        ];
        var executed = new List<StartupMacroActionType?>();
        bool? completed = null;
        var executor = new StartupMacroExecutor(
            actions,
            () => new HashSet<int> { 42 },
            _ => throw new Xunit.Sdk.XunitException("Unexpected macro failure."),
            succeeded => completed = succeeded,
            (action, _) =>
            {
                executed.Add(action.Type);
                return StartupMacroWindowActions.ExecutionResult.Success("queued");
            }
        );
        DateTime started = new(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

        executor.Start();
        executor.Advance(started);
        executor.Advance(started.AddMilliseconds(1_999));
        Assert.Empty(executed);
        Assert.True(executor.Pending);

        executor.Advance(started.AddMilliseconds(2_000));

        Assert.Equal(
            [StartupMacroActionType.Hotkey, StartupMacroActionType.Hotkey],
            executed
        );
        Assert.False(executor.Pending);
        Assert.True(completed);
    }

    [Fact]
    public void Advance_FailedAction_ReportsAndContinues()
    {
        List<StartupMacroActionConfig> actions =
        [
            new() { Type = StartupMacroActionType.Hotkey, Keys = ["F7"] },
            new() { Type = StartupMacroActionType.BringToFront }
        ];
        var failures = new List<string>();
        var executed = new List<StartupMacroActionType?>();
        bool? completed = null;
        var executor = new StartupMacroExecutor(
            actions,
            () => new HashSet<int> { 42 },
            failures.Add,
            succeeded => completed = succeeded,
            (action, _) =>
            {
                executed.Add(action.Type);
                return action.Type == StartupMacroActionType.Hotkey
                    ? StartupMacroWindowActions.ExecutionResult.Failure("rejected")
                    : StartupMacroWindowActions.ExecutionResult.Success("moved");
            }
        );

        executor.Start();
        executor.Advance(DateTime.UtcNow);

        Assert.Equal(2, executed.Count);
        Assert.Single(failures);
        Assert.Contains("action 1 (Hotkey)", failures[0]);
        Assert.False(completed);
    }
}
