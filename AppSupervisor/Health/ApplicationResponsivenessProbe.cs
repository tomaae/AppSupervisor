using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AppSupervisor.Health;

/// <summary>
/// Checks that every top-level window owned by a helper process is still processing Windows messages.
/// </summary>
internal sealed class ApplicationResponsivenessProbe : IHealthProbe
{
    private const uint NativeWindowTimeoutMilliseconds = 2000;

    private readonly Func<IReadOnlySet<int>, IReadOnlyList<IntPtr>> _windowProvider;
    private readonly Func<IntPtr, bool> _responsivenessProvider;

    /// <summary>Creates a probe backed by native desktop-window enumeration and timeout-bounded messages.</summary>
    public ApplicationResponsivenessProbe()
        : this(FindOwnedWindows, IsWindowResponsive)
    {
    }

    /// <summary>Creates a responsiveness probe with injectable native operations for deterministic tests.</summary>
    /// <param name="windowProvider">Finds top-level windows owned by the supplied process identifiers.</param>
    /// <param name="responsivenessProvider">Returns whether one window processes the harmless probe message.</param>
    internal ApplicationResponsivenessProbe(
        Func<IReadOnlySet<int>, IReadOnlyList<IntPtr>> windowProvider,
        Func<IntPtr, bool> responsivenessProvider)
    {
        _windowProvider = windowProvider;
        _responsivenessProvider = responsivenessProvider;
    }

    /// <summary>Checks all currently owned helper windows without blocking the supervision thread.</summary>
    /// <param name="ownerProcessIds">The currently running helper process identifiers.</param>
    /// <param name="cancellationToken">Cancels the probe on pause, reload, shutdown, or deactivation.</param>
    /// <returns>A healthy result only when every discoverable owned window responds.</returns>
    public async Task<HealthProbeResult> CheckAsync(
        IReadOnlySet<int> ownerProcessIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ownerProcessIds.Count == 0)
            return HealthProbeResult.Failure("The helper process is not running.");

        IReadOnlyList<IntPtr> windows = _windowProvider(ownerProcessIds);

        if (windows.Count == 0)
        {
            return HealthProbeResult.Success(
                "The helper exposes no top-level window, so responsiveness cannot be measured."
            );
        }

        Task<(IntPtr Window, bool Responsive)>[] probeTasks = windows
            .Select(window => Task.Run(
                () => (window, _responsivenessProvider(window)),
                CancellationToken.None
            ))
            .ToArray();

        (IntPtr Window, bool Responsive)[] results = await Task.WhenAll(probeTasks)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        int failedWindows = results.Count(result => !result.Responsive);

        return failedWindows == 0
            ? HealthProbeResult.Success(
                windows.Count == 1
                    ? "The helper window is responding."
                    : string.Concat("All ", windows.Count, " helper windows are responding.")
            )
            : HealthProbeResult.Failure(
                failedWindows == 1
                    ? "One helper window is not responding."
                    : string.Concat(failedWindows, " helper windows are not responding.")
            );
    }

    /// <summary>Clears retained probe state; responsiveness probes retain no state between samples.</summary>
    public void Reset()
    {
    }

    /// <summary>Releases probe resources; native responsiveness probes retain no owned resources.</summary>
    public void Dispose()
    {
    }

    /// <summary>Enumerates desktop and message-only top-level windows owned by the supplied helper processes.</summary>
    /// <param name="ownerProcessIds">The helper process identifiers whose windows are required.</param>
    /// <returns>A stable snapshot of matching native window handles.</returns>
    private static IReadOnlyList<IntPtr> FindOwnedWindows(IReadOnlySet<int> ownerProcessIds)
    {
        var windows = new HashSet<IntPtr>();
        bool completed = NativeMethods.EnumWindows((window, _) =>
        {
            AddOwnedWindow(windows, window, ownerProcessIds);
            return true;
        }, IntPtr.Zero);

        if (!completed)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows could not enumerate application windows."
            );
        }

        IntPtr messageWindow = IntPtr.Zero;

        while (true)
        {
            messageWindow = NativeMethods.FindWindowEx(
                NativeMethods.HWND_MESSAGE,
                messageWindow,
                null,
                null
            );

            if (messageWindow == IntPtr.Zero)
                break;

            AddOwnedWindow(windows, messageWindow, ownerProcessIds);
        }

        return windows.ToArray();
    }

    /// <summary>Adds one window when its owning process belongs to the managed helper.</summary>
    /// <param name="windows">The deduplicated destination window set.</param>
    /// <param name="window">The candidate native window handle.</param>
    /// <param name="ownerProcessIds">The managed helper process identifiers.</param>
    private static void AddOwnedWindow(
        ISet<IntPtr> windows,
        IntPtr window,
        IReadOnlySet<int> ownerProcessIds)
    {
        NativeMethods.GetWindowThreadProcessId(window, out uint processId);

        if (processId <= int.MaxValue && ownerProcessIds.Contains((int)processId))
            windows.Add(window);
    }

    /// <summary>Sends a harmless timeout-bounded message to determine whether one window is processing messages.</summary>
    /// <param name="window">The top-level window to probe.</param>
    /// <returns><see langword="true"/> when the window processes the message before the timeout.</returns>
    private static bool IsWindowResponsive(IntPtr window)
    {
        IntPtr sendResult = NativeMethods.SendMessageTimeout(
            window,
            NativeMethods.WM_NULL,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.SendMessageTimeoutFlags.Block |
                NativeMethods.SendMessageTimeoutFlags.AbortIfHung |
                NativeMethods.SendMessageTimeoutFlags.ErrorOnExit,
            NativeWindowTimeoutMilliseconds,
            out _
        );

        return sendResult != IntPtr.Zero;
    }
}
