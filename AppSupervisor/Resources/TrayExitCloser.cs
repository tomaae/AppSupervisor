using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace AppSupervisor.Resources;

/// <summary>
/// Requests a Qt tray application's own Quit or Exit command without blocking supervision.
/// </summary>
internal static class TrayExitCloser
{
    private const uint WmContextMenu = 0x007B;
    private const uint WmCancelMode = 0x001F;
    private const uint QtTrayCallbackMessage = 0x8000 + 101;
    private const string QtTrayWindowClass = "QTrayIconMessageWindowClass";

    /// <summary>
    /// Opens an owned Qt tray menu and invokes only an accessible menu item explicitly named Quit or Exit.
    /// </summary>
    /// <param name="processId">The process whose tray menu may be used.</param>
    /// <param name="cancellationToken">Cancels the request before it invokes an application command.</param>
    /// <returns><see langword="true"/> when a matching menu command was invoked.</returns>
    internal static async Task<bool> TryRequestExitAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        bool commandInvoked = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            IntPtr trayWindow = FindOwnedWindow(
                processId,
                static (_, className) =>
                    string.Equals(
                        className,
                        QtTrayWindowClass,
                        StringComparison.Ordinal));

            if (trayWindow == IntPtr.Zero ||
                !GetCursorPos(out NativePoint cursorPosition))
            {
                return false;
            }

            IntPtr packedCursorPosition = PackCoordinates(cursorPosition);

            if (!PostMessage(
                trayWindow,
                QtTrayCallbackMessage,
                packedCursorPosition,
                new IntPtr(WmContextMenu)))
            {
                return false;
            }

            for (int attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                foreach (IntPtr popupWindow in FindOwnedPopupWindows(processId))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!TryInvokeExitCommand(
                        popupWindow,
                        cancellationToken))
                    {
                        continue;
                    }

                    commandInvoked = true;
                    return true;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is expected when another profile needs the helper or supervision reloads.
        }
        catch
        {
            // This optional graceful fallback must never escape into the supervisor loop.
        }
        finally
        {
            if (!commandInvoked)
                DismissOwnedPopupWindows(processId);
        }

        return false;
    }

    /// <summary>Recognizes only explicit, non-destructive application termination labels.</summary>
    /// <param name="name">The accessible menu-item name to inspect.</param>
    /// <returns><see langword="true"/> for Quit or Exit after normalizing menu access-key markers.</returns>
    internal static bool IsExitCommandName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        string normalized = name
            .Replace("&", string.Empty, StringComparison.Ordinal)
            .Trim()
            .TrimEnd('.');

        return string.Equals(normalized, "Quit", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "Exit", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Finds the first top-level window owned by a process that satisfies a supplied predicate.</summary>
    /// <param name="processId">The required window-owner process identifier.</param>
    /// <param name="predicate">Checks the window handle and native class name.</param>
    /// <returns>The matching window handle, or zero when none is found.</returns>
    private static IntPtr FindOwnedWindow(
        int processId,
        Func<IntPtr, string, bool> predicate)
    {
        IntPtr result = IntPtr.Zero;

        EnumWindows((window, _) =>
        {
            uint threadId = GetWindowThreadProcessId(window, out uint ownerProcessId);
            if (threadId == 0 || ownerProcessId != (uint)processId)
                return true;

            string className = GetNativeClassName(window);

            if (!predicate(window, className))
                return true;

            result = window;
            return false;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>Collects visible native or Qt popup windows owned by the target process.</summary>
    /// <param name="processId">The required popup-owner process identifier.</param>
    /// <returns>Candidate popup handles that may expose an accessible Exit or Quit item.</returns>
    private static IReadOnlyList<IntPtr> FindOwnedPopupWindows(int processId)
    {
        var results = new List<IntPtr>();

        EnumWindows((window, _) =>
        {
            uint threadId = GetWindowThreadProcessId(window, out uint ownerProcessId);
            if (threadId == 0 ||
                ownerProcessId != (uint)processId ||
                !IsWindowVisible(window))
            {
                return true;
            }

            string className = GetNativeClassName(window);
            bool nativePopup = string.Equals(
                className,
                "#32768",
                StringComparison.Ordinal);
            bool qtPopup = className.StartsWith("Qt", StringComparison.Ordinal) &&
                className.Contains("Popup", StringComparison.OrdinalIgnoreCase);

            if (nativePopup || qtPopup)
                results.Add(window);

            return true;
        }, IntPtr.Zero);

        return results;
    }
    /// <summary>Dismisses popup menus opened by a failed or cancelled tray close attempt.</summary>
    /// <param name="processId">The process whose owned popup menus should be dismissed.</param>
    private static void DismissOwnedPopupWindows(int processId)
    {
        foreach (IntPtr popupWindow in FindOwnedPopupWindows(processId))
        {
            PostMessage(
                popupWindow,
                WmCancelMode,
                IntPtr.Zero,
                IntPtr.Zero);
        }
    }


    /// <summary>Invokes an enabled menu item only when UI Automation identifies it as Quit or Exit.</summary>
    /// <param name="popupWindow">The owned popup window to inspect.</param>
    /// <param name="cancellationToken">Prevents invocation after the close request is cancelled.</param>
    /// <returns><see langword="true"/> when an explicit Exit or Quit action was invoked.</returns>
    private static bool TryInvokeExitCommand(
        IntPtr popupWindow,
        CancellationToken cancellationToken)
    {
        AutomationElement root = AutomationElement.FromHandle(popupWindow);
        AutomationElementCollection descendants = root.FindAll(
            TreeScope.Descendants,
            Condition.TrueCondition);

        foreach (AutomationElement element in descendants)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsExitCommandName(element.Current.Name) ||
                element.Current.ControlType != ControlType.MenuItem ||
                !element.Current.IsEnabled)
            {
                continue;
            }

            if (!element.TryGetCurrentPattern(
                InvokePattern.Pattern,
                out object? pattern) ||
                pattern is not InvokePattern invokePattern)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            invokePattern.Invoke();
            return true;
        }

        return false;
    }

    /// <summary>Reads one native window class name without leaking a mutable buffer.</summary>
    /// <param name="window">The window whose class name is requested.</param>
    /// <returns>The native class name, or an empty string when Windows cannot provide it.</returns>
    private static string GetNativeClassName(IntPtr window)
    {
        var className = new StringBuilder(256);
        GetClassName(window, className, className.Capacity);
        return className.ToString();
    }

    /// <summary>Packs signed screen coordinates into the callback format expected by Qt.</summary>
    /// <param name="point">The current cursor position in screen coordinates.</param>
    /// <returns>The coordinates packed into a native message parameter.</returns>
    private static IntPtr PackCoordinates(NativePoint point)
    {
        long packed = ((long)(ushort)point.Y << 16) | (ushort)point.X;
        return new IntPtr(packed);
    }

    /// <summary>Receives each top-level window enumerated by Windows.</summary>
    /// <param name="window">The current top-level window.</param>
    /// <param name="parameter">Caller-defined enumeration data.</param>
    /// <returns><see langword="true"/> to continue enumeration.</returns>
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    /// <summary>Stores a cursor position returned by the native Windows API.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        /// <summary>Gets or sets the horizontal screen coordinate.</summary>
        internal int X;

        /// <summary>Gets or sets the vertical screen coordinate.</summary>
        internal int Y;
    }

    /// <summary>Enumerates desktop top-level windows.</summary>
    /// <param name="callback">The callback invoked for each window.</param>
    /// <param name="parameter">Caller-defined callback data.</param>
    /// <returns><see langword="true"/> when enumeration completed.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsProc callback,
        IntPtr parameter);

    /// <summary>Retrieves the native class name for a window.</summary>
    /// <param name="window">The window to inspect.</param>
    /// <param name="className">Receives the class name.</param>
    /// <param name="capacity">The receiving buffer capacity.</param>
    /// <returns>The number of copied characters.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        IntPtr window,
        StringBuilder className,
        int capacity);

    /// <summary>Retrieves the owning process identifier for a window.</summary>
    /// <param name="window">The window to inspect.</param>
    /// <param name="processId">Receives the process identifier.</param>
    /// <returns>The owning thread identifier.</returns>
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);

    /// <summary>Determines whether a top-level popup is currently visible.</summary>
    /// <param name="window">The window to inspect.</param>
    /// <returns><see langword="true"/> when the window is visible.</returns>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    /// <summary>Retrieves the current cursor position in screen coordinates.</summary>
    /// <param name="point">Receives the cursor position.</param>
    /// <returns><see langword="true"/> when the position was retrieved.</returns>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    /// <summary>Posts a native message without waiting for the destination application.</summary>
    /// <param name="window">The destination window.</param>
    /// <param name="message">The native message identifier.</param>
    /// <param name="wordParameter">Message-specific word data.</param>
    /// <param name="longParameter">Message-specific long data.</param>
    /// <returns><see langword="true"/> when the message was queued.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        IntPtr window,
        uint message,
        IntPtr wordParameter,
        IntPtr longParameter);
}
