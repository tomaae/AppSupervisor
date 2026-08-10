using System.Runtime.InteropServices;

namespace AppSupervisor;

/// <summary>
/// Provides the Win32 window-management functions used to close, minimize, and probe managed applications.
/// </summary>
internal static class NativeMethods
{
    public const int SW_MINIMIZE = 6;
    public const uint WM_NULL = 0x0000;
    public const uint WM_CLOSE = 0x0010;
    /// <summary>Identifies the shared parent used by Windows message-only windows.</summary>
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    /// <summary>Selects timeout and hung-window handling for <see cref="SendMessageTimeout"/>.</summary>
    [Flags]
    public enum SendMessageTimeoutFlags : uint
    {
        /// <summary>Blocks reentrant nonqueued message processing while the probe is active.</summary>
        Block = 0x0001,

        /// <summary>Returns early when Windows already considers the destination thread hung.</summary>
        AbortIfHung = 0x0002,

        /// <summary>Reports failure when the destination window or thread exits during the probe.</summary>
        ErrorOnExit = 0x0020
    }

    /// <summary>
    /// Receives each top-level window discovered by <see cref="EnumWindows"/>.
    /// </summary>
    /// <param name="hWnd">The handle of the current top-level window.</param>
    /// <param name="lParam">Caller-provided data passed through the enumeration.</param>
    /// <returns><see langword="true"/> to continue enumerating; otherwise, <see langword="false"/>.</returns>
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>
    /// Determines whether a native window is minimized.
    /// </summary>
    /// <param name="hWnd">The window handle to inspect.</param>
    /// <returns><see langword="true"/> when the window is minimized.</returns>
    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    /// <summary>
    /// Enumerates all top-level windows on the desktop.
    /// </summary>
    /// <param name="lpEnumFunc">The callback invoked for each discovered window.</param>
    /// <param name="lParam">Caller-provided data passed to the callback.</param>
    /// <returns><see langword="true"/> when enumeration completes successfully.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumWindows(
        EnumWindowsProc lpEnumFunc,
        IntPtr lParam
    );

    /// <summary>Finds a child window after a previous child, including children of the message-only window parent.</summary>
    /// <param name="parentWindow">The parent whose direct children are searched.</param>
    /// <param name="childAfter">The previous child, or zero to begin the search.</param>
    /// <param name="className">An optional window class name.</param>
    /// <param name="windowName">An optional window caption.</param>
    /// <returns>The next matching window, or zero when no further match exists.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowEx(
        IntPtr parentWindow,
        IntPtr childAfter,
        string? className,
        string? windowName
    );

    /// <summary>
    /// Retrieves the process identifier that owns a native window.
    /// </summary>
    /// <param name="hWnd">The window handle to inspect.</param>
    /// <param name="lpdwProcessId">Receives the owning process identifier.</param>
    /// <returns>The identifier of the thread that created the window.</returns>
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(
        IntPtr hWnd,
        out uint lpdwProcessId
    );

    /// <summary>
    /// Determines whether a native window is currently visible.
    /// </summary>
    /// <param name="hWnd">The window handle to inspect.</param>
    /// <returns><see langword="true"/> when the window is visible.</returns>
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>
    /// Changes the display state of a native window.
    /// </summary>
    /// <param name="hWnd">The window handle to update.</param>
    /// <param name="nCmdShow">The requested display-state command.</param>
    /// <returns><see langword="true"/> when the window was previously visible.</returns>
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(
        IntPtr hWnd,
        int nCmdShow
    );

    /// <summary>
    /// Posts an asynchronous message to a native window's message queue.
    /// </summary>
    /// <param name="hWnd">The destination window handle.</param>
    /// <param name="msg">The native message identifier.</param>
    /// <param name="wParam">Message-specific word data.</param>
    /// <param name="lParam">Message-specific long data.</param>
    /// <returns><see langword="true"/> when the message was posted successfully.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam
    );

    /// <summary>
    /// Sends a synchronous message with a strict timeout so an unresponsive destination cannot block AppSupervisor.
    /// </summary>
    /// <param name="hWnd">The destination window handle.</param>
    /// <param name="msg">The native message identifier.</param>
    /// <param name="wParam">Message-specific word data.</param>
    /// <param name="lParam">Message-specific long data.</param>
    /// <param name="flags">The timeout and hung-window behavior.</param>
    /// <param name="timeoutMilliseconds">The maximum native wait in milliseconds.</param>
    /// <param name="result">Receives the destination window procedure's result.</param>
    /// <returns>A nonzero value when the destination processed the message.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        SendMessageTimeoutFlags flags,
        uint timeoutMilliseconds,
        out IntPtr result
    );
    /// Releases a native icon handle created from a bitmap.

    /// <summary>
    /// </summary>
    /// <param name="hIcon">The icon handle to release.</param>
    /// <returns><see langword="true"/> when the handle was destroyed successfully.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);
}
