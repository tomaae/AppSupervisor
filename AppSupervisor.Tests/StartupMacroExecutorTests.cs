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

    [Fact]
    public void Advance_WindowUnavailableAfterArbitraryElapsedTime_RemainsPending()
    {
        List<StartupMacroActionConfig> actions =
        [
            new() { Type = StartupMacroActionType.MoveWindow, X = 0, Y = 0 }
        ];
        var failures = new List<string>();
        int attempts = 0;
        bool? completed = null;
        var executor = new StartupMacroExecutor(
            actions,
            () => new HashSet<int> { 42 },
            failures.Add,
            succeeded => completed = succeeded,
            (_, _) =>
            {
                attempts++;
                return StartupMacroWindowActions.ExecutionResult.Unavailable(
                    "No visible top-level helper window is available yet."
                );
            }
        );
        DateTime started = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

        executor.Start();
        executor.Advance(started);
        executor.Advance(started.AddDays(1));

        Assert.True(executor.Pending);
        Assert.Null(completed);
        Assert.Equal(2, attempts);
        Assert.Empty(failures);
    }

    [Fact]
    public void Advance_GeometryAdjustment_WaitsForRepeatedStableObservations()
    {
        List<StartupMacroActionConfig> actions =
        [
            new() { Type = StartupMacroActionType.ResizeWindow, Width = 800, Height = 600 }
        ];
        var failures = new List<string>();
        int attempts = 0;
        bool? completed = null;
        var executor = new StartupMacroExecutor(
            actions,
            () => new HashSet<int> { 42 },
            failures.Add,
            succeeded => completed = succeeded,
            (_, _) => ++attempts == 1
                ? StartupMacroWindowActions.ExecutionResult.Adjusted("resized")
                : StartupMacroWindowActions.ExecutionResult.Success("stable")
        );
        DateTime started = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

        executor.Start();
        executor.Advance(started);
        executor.Advance(started.AddHours(1));
        executor.Advance(started.AddHours(1).AddSeconds(2));

        Assert.True(executor.Pending);
        Assert.Null(completed);

        executor.Advance(started.AddHours(1).AddSeconds(3));

        Assert.False(executor.Pending);
        Assert.True(completed);
        Assert.Equal(4, attempts);
        Assert.Empty(failures);
    }

    [Fact]
    public void Advance_OtherWindowAction_RetriesUntilWindowAppears()
    {
        List<StartupMacroActionConfig> actions =
        [
            new() { Type = StartupMacroActionType.Maximize }
        ];
        var failures = new List<string>();
        bool windowAvailable = false;
        bool? completed = null;
        var executor = new StartupMacroExecutor(
            actions,
            () => new HashSet<int> { 42 },
            failures.Add,
            succeeded => completed = succeeded,
            (_, _) => windowAvailable
                ? StartupMacroWindowActions.ExecutionResult.Success("maximized")
                : StartupMacroWindowActions.ExecutionResult.Unavailable("not ready")
        );
        DateTime started = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

        executor.Start();
        executor.Advance(started);
        executor.Advance(started.AddDays(1));
        Assert.True(executor.Pending);
        Assert.Null(completed);
        Assert.Empty(failures);

        windowAvailable = true;
        executor.Advance(started.AddDays(1).AddSeconds(1));

        Assert.False(executor.Pending);
        Assert.True(completed);
        Assert.Empty(failures);
    }

    [Fact]
    public void Advance_Minimize_WaitsForRegularRetriesBeforeNextAction()
    {
        var executed = new List<StartupMacroActionType?>();
        bool? completed = null;
        var executor = new StartupMacroExecutor(
            [new() { Type = StartupMacroActionType.Minimize }, new() { Type = StartupMacroActionType.Restore }],
            () => new HashSet<int> { 42 },
            _ => throw new Xunit.Sdk.XunitException("Unexpected macro failure."),
            succeeded => completed = succeeded,
            (action, _) =>
            {
                executed.Add(action.Type);
                return StartupMacroWindowActions.ExecutionResult.Success("applied");
            }
        );
        DateTime started = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        executor.Start();
        executor.Advance(started);
        Assert.Empty(executed);
        for (int milliseconds = 250; milliseconds <= 750; milliseconds += 250)
            executor.Advance(started.AddMilliseconds(milliseconds));
        Assert.True(executor.Pending);
        Assert.Null(completed);
        Assert.DoesNotContain(StartupMacroActionType.Restore, executed);

        executor.Advance(started.AddSeconds(1));

        Assert.True(completed);
        Assert.False(executor.Pending);
        Assert.Equal(4, executed.Count(type => type == StartupMacroActionType.Minimize));
        Assert.Equal(StartupMacroActionType.Restore, executed[^1]);
    }

    [Fact]
    public void Advance_MinimizeTimeout_ReportsOnceAndContinues()
    {
        var failures = new List<string>();
        bool restored = false;
        bool? completed = null;
        var executor = new StartupMacroExecutor(
            [new() { Type = StartupMacroActionType.Minimize }, new() { Type = StartupMacroActionType.Restore }],
            () => new HashSet<int> { 42 },
            failures.Add,
            succeeded => completed = succeeded,
            (action, _) =>
            {
                if (action.Type == StartupMacroActionType.Minimize)
                    return StartupMacroWindowActions.ExecutionResult.Unavailable("no window");
                restored = true;
                return StartupMacroWindowActions.ExecutionResult.Success("restored");
            }
        );
        DateTime started = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        executor.Start();
        executor.Advance(started);
        executor.Advance(started.AddMilliseconds(9_999));
        Assert.True(executor.Pending);
        Assert.False(restored);
        Assert.Empty(failures);

        executor.Advance(started.AddMilliseconds(10_250));
        executor.Advance(started.AddSeconds(11));

        Assert.False(executor.Pending);
        Assert.False(completed);
        Assert.True(restored);
        Assert.Contains("action 1 (Minimize)", Assert.Single(failures));
        Assert.Contains("10 seconds", failures[0]);
    }

    [Fact]
    public void Start_AfterPartialMinimize_DiscardsPreviousRetryState()
    {
        bool? completed = null;
        var executor = new StartupMacroExecutor(
            [new() { Type = StartupMacroActionType.Minimize }],
            () => new HashSet<int> { 42 },
            _ => throw new Xunit.Sdk.XunitException("Unexpected macro failure."),
            succeeded => completed = succeeded,
            (_, _) => StartupMacroWindowActions.ExecutionResult.Success("minimized")
        );
        DateTime started = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        executor.Start();
        for (int milliseconds = 0; milliseconds <= 750; milliseconds += 250)
            executor.Advance(started.AddMilliseconds(milliseconds));

        executor.Cancel();
        executor.Advance(started.AddSeconds(1));
        Assert.Null(completed);
        executor.Start();
        for (int milliseconds = 20_000; milliseconds <= 20_750; milliseconds += 250)
            executor.Advance(started.AddMilliseconds(milliseconds));
        Assert.True(executor.Pending);
        Assert.Null(completed);

        executor.Advance(started.AddSeconds(21));
        Assert.True(completed);
        Assert.False(executor.Pending);
    }
}
