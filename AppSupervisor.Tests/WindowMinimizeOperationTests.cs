using AppSupervisor.Resources;
using System.Drawing;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Exercises the shared minimizer against real multi-window helpers and delayed startup.</summary>
[Collection(WinFormsTestCollection.Name)]
public sealed class WindowMinimizeOperationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Minimize_MultipleVisibleWindows_UsesRegularBehavior(bool macro)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var first = CreateWindow();
                using var second = CreateWindow();
                using var hidden = CreateWindow();
                IntPtr hiddenHandle = hidden.Handle;
                first.Show();
                second.Show();
                Application.DoEvents();
                IReadOnlySet<int> processIds = new HashSet<int> { Environment.ProcessId };
                var action = new StartupMacroActionConfig { Type = StartupMacroActionType.Minimize };

                // The old macro rejected these two windows before trying to minimize either.
                Assert.False(NativeMethods.IsIconic(first.Handle));
                Assert.False(NativeMethods.IsIconic(second.Handle));
                Assert.False(WindowMinimizeOperation.MinimizeProcessWindows(int.MaxValue));
                Assert.False(NativeMethods.IsIconic(first.Handle));
                Assert.False(NativeMethods.IsIconic(second.Handle));

                if (macro)
                {
                    var failures = new List<string>();
                    bool? completed = null;
                    var executor = new StartupMacroExecutor(
                        [new() { Type = StartupMacroActionType.MoveWindow, X = 0, Y = 0 }, action],
                        () => processIds,
                        failures.Add,
                        succeeded => completed = succeeded
                    );
                    DateTime started = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
                    executor.Start();
                    for (int milliseconds = 0; milliseconds <= 1_000; milliseconds += 250)
                        executor.Advance(started.AddMilliseconds(milliseconds));

                    Assert.False(executor.Pending);
                    Assert.False(completed); // Move remains ambiguous; Minimize must still succeed.
                    Assert.Contains("action 1 (MoveWindow)", Assert.Single(failures));
                    Assert.Contains("2 eligible top-level windows", failures[0]);
                }
                else
                {
                    Assert.True(WindowMinimizeOperation.MinimizeProcessWindows(Environment.ProcessId));
                }

                Assert.True(NativeMethods.IsIconic(first.Handle));
                Assert.True(NativeMethods.IsIconic(second.Handle));
                Assert.False(NativeMethods.IsWindowVisible(hiddenHandle));
                Assert.False(NativeMethods.IsIconic(hiddenHandle));
                Assert.True(StartupMacroWindowActions.Execute(action, processIds).AppliedSuccessfully);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Native minimization test timed out.");
        Assert.Null(failure);
    }

    [Fact]
    public void Advance_WindowDisappearsDuringStartup_RetriesWithoutPrematureCompletion()
    {
        DateTime started = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        var operation = new WindowMinimizeOperation(started);
        bool available = false;
        int attempts = 0;
        bool Minimize() { attempts++; return available; }

        Assert.Null(operation.Advance(started, Minimize));
        Assert.Null(operation.Advance(started.AddMilliseconds(249), Minimize));
        Assert.Equal(0, attempts);
        Assert.Null(operation.Advance(started.AddMilliseconds(250), Minimize));
        Assert.Null(operation.Advance(started.AddMilliseconds(251), Minimize));
        Assert.Equal(1, attempts);

        available = true;
        Assert.Null(operation.Advance(started.AddMilliseconds(500), Minimize));
        Assert.Null(operation.Advance(started.AddMilliseconds(750), Minimize));
        available = false;
        Assert.Null(operation.Advance(started.AddMilliseconds(1_000), Minimize));
        available = true;
        Assert.Null(operation.Advance(started.AddMilliseconds(1_250), Minimize));
        Assert.Null(operation.Advance(started.AddMilliseconds(1_500), Minimize));
        Assert.Null(operation.Advance(started.AddMilliseconds(1_750), Minimize));
        Assert.True(operation.Advance(started.AddMilliseconds(2_000), Minimize));
    }

    private static Form CreateWindow() => new()
    {
        ShowInTaskbar = true,
        Opacity = 0,
        StartPosition = FormStartPosition.Manual,
        Location = new Point(-10_000, -10_000)
    };
}
