namespace AppSupervisor.Resources;

/// <summary>Shares the regular post-launch minimization and retry behavior with startup macros.</summary>
internal sealed class WindowMinimizeOperation(DateTime startedUtc)
{
    internal const int TimeoutMilliseconds = 10_000;
    internal const int CheckIntervalMilliseconds = 250;
    private const int StableMillisecondsRequired = 1_000;

    private DateTime _nextCheckUtc = startedUtc.AddMilliseconds(CheckIntervalMilliseconds);
    private int _minimizedStableMilliseconds;

    /// <summary>Returns null while pending, true when stable, or false on timeout.</summary>
    internal bool? Advance(DateTime nowUtc, Func<bool> minimize)
    {
        if (nowUtc < _nextCheckUtc)
            return null;

        if (nowUtc - startedUtc >= TimeSpan.FromMilliseconds(TimeoutMilliseconds))
            return false;

        if (minimize())
        {
            _minimizedStableMilliseconds += CheckIntervalMilliseconds;

            if (_minimizedStableMilliseconds >= StableMillisecondsRequired)
                return true;
        }
        else
        {
            _minimizedStableMilliseconds = 0;
        }

        _nextCheckUtc = nowUtc.AddMilliseconds(CheckIntervalMilliseconds);
        return null;
    }

    /// <summary>Minimizes all visible top-level windows belonging to a process.</summary>
    /// <returns>Whether at least one matching window is minimized.</returns>
    internal static bool MinimizeProcessWindows(int processId)
    {
        bool minimized = false;

        NativeMethods.EnumWindows((hWnd, lParam) =>
        {
            uint windowThreadId = NativeMethods.GetWindowThreadProcessId(
                hWnd,
                out uint windowProcessId
            );

            if (windowThreadId == 0 ||
                windowProcessId != (uint)processId ||
                !NativeMethods.IsWindowVisible(hWnd))
                return true;

            if (NativeMethods.IsIconic(hWnd))
            {
                minimized = true;
                return true;
            }

            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_MINIMIZE);

            if (NativeMethods.IsIconic(hWnd))
                minimized = true;

            return true;
        }, IntPtr.Zero);

        return minimized;
    }
}
