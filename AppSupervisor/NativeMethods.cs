using System.Runtime.InteropServices;

namespace AppSupervisor;

/// <summary>
/// Provides the Win32 window-management functions used to close and minimize managed applications.
/// </summary>
internal static class NativeMethods
{
    public const int SW_MINIMIZE = 6;
    public const uint WM_CLOSE = 0x0010;

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
    [DllImport("user32.dll")]
    public static extern bool EnumWindows(
        EnumWindowsProc lpEnumFunc,
        IntPtr lParam
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
    /// Releases a native icon handle created from a bitmap.
    /// </summary>
    /// <param name="hIcon">The icon handle to release.</param>
    /// <returns><see langword="true"/> when the handle was destroyed successfully.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);
}
