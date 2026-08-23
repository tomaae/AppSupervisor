using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AppSupervisor;

/// <summary>Executes focus-preserving native window actions for startup macros and editor tests.</summary>
internal static class StartupMacroWindowActions
{
    internal enum ExecutionStatus
    {
        Succeeded,
        WindowAdjusted,
        WindowUnavailable,
        Failed
    }

    internal readonly record struct ExecutionResult(ExecutionStatus Status, string Detail)
    {
        public bool AppliedSuccessfully =>
            Status is ExecutionStatus.Succeeded or ExecutionStatus.WindowAdjusted;

        public static ExecutionResult Success(string detail) =>
            new(ExecutionStatus.Succeeded, detail);

        public static ExecutionResult Adjusted(string detail) =>
            new(ExecutionStatus.WindowAdjusted, detail);

        public static ExecutionResult Unavailable(string detail) =>
            new(ExecutionStatus.WindowUnavailable, detail);

        public static ExecutionResult Failure(string detail) =>
            new(ExecutionStatus.Failed, detail);
    }

    /// <summary>Executes one non-delay action against exactly one visible helper window.</summary>
    internal static ExecutionResult Execute(
        StartupMacroActionConfig action,
        IReadOnlySet<int> processIds)
    {
        if (action.Type is not StartupMacroActionType type)
            return ExecutionResult.Failure("The action type is missing.");

        if (type == StartupMacroActionType.Delay)
            return ExecutionResult.Success("Delay completed.");

        if (type == StartupMacroActionType.Hotkey)
        {
            if (processIds.Count == 0)
                return ExecutionResult.Unavailable("The helper process is not running.");

            return SendHotkey(action.Keys ?? []);
        }

        ExecutionResult targetResult = FindUniqueWindow(processIds, out IntPtr window);
        if (targetResult.Status != ExecutionStatus.Succeeded)
            return targetResult;

        try
        {
            return type switch
            {
                StartupMacroActionType.MoveWindow => MoveWindow(window, action),
                StartupMacroActionType.ResizeWindow => ResizeWindow(window, action),
                StartupMacroActionType.Minimize => ChangeWindowState(window, NativeMethods.SC_MINIMIZE, "minimize"),
                StartupMacroActionType.Maximize => ChangeWindowState(window, NativeMethods.SC_MAXIMIZE, "maximize"),
                StartupMacroActionType.Restore => ChangeWindowState(window, NativeMethods.SC_RESTORE, "restore"),
                StartupMacroActionType.BringToFront => BringToFront(window),
                _ => ExecutionResult.Failure($"Unsupported startup macro action '{type}'.")
            };
        }
        catch (Exception exception)
        {
            return ExecutionResult.Failure(exception.Message);
        }
    }

    /// <summary>Returns whether a captured key changes another key's interpretation.</summary>
    internal static bool IsModifierKey(Keys key) =>
        key is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or
        Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or
        Keys.Menu or Keys.LMenu or Keys.RMenu;

    /// <summary>Formats a captured chord consistently in lists and diagnostics.</summary>
    internal static string FormatHotkey(IEnumerable<string>? keys)
    {
        if (keys is null)
            return "No keys";

        return string.Join("+", keys.Select(key => key switch
        {
            nameof(Keys.ControlKey) or nameof(Keys.LControlKey) or nameof(Keys.RControlKey) => "Ctrl",
            nameof(Keys.ShiftKey) or nameof(Keys.LShiftKey) or nameof(Keys.RShiftKey) => "Shift",
            nameof(Keys.Menu) or nameof(Keys.LMenu) or nameof(Keys.RMenu) => "Alt",
            _ => key
        }));
    }

    /// <summary>Reads one normal visible helper window's current outer bounds.</summary>
    internal static ExecutionResult ReadCurrentWindowBounds(
        IReadOnlySet<int> processIds,
        out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        ExecutionResult targetResult = FindUniqueWindow(processIds, out IntPtr window);
        if (targetResult.Status != ExecutionStatus.Succeeded)
            return targetResult;

        if (NativeMethods.IsIconic(window) || NativeMethods.IsZoomed(window))
        {
            return ExecutionResult.Failure(
                "Restore the helper window to its normal state before reading its position or size."
            );
        }

        if (!NativeMethods.GetWindowRect(window, out NativeMethods.RECT rectangle))
            return ExecutionResult.Failure(new Win32Exception().Message);

        bounds = Rectangle.FromLTRB(
            rectangle.Left,
            rectangle.Top,
            rectangle.Right,
            rectangle.Bottom
        );
        return ExecutionResult.Success("Current helper window bounds read.");
    }

    /// <summary>Converts absolute window coordinates to the selected monitor work area's origin.</summary>
    internal static Point ToMonitorRelativePosition(Rectangle bounds, Rectangle workingArea) =>
        new(bounds.Left - workingArea.Left, bounds.Top - workingArea.Top);

    private static ExecutionResult FindUniqueWindow(
        IReadOnlySet<int> processIds,
        out IntPtr window)
    {
        window = IntPtr.Zero;

        if (processIds.Count == 0)
            return ExecutionResult.Unavailable("The helper process is not running.");

        var candidates = new List<IntPtr>();
        bool enumerated = NativeMethods.EnumWindows((candidate, _) =>
        {
            uint threadId = NativeMethods.GetWindowThreadProcessId(candidate, out uint processId);

            if (threadId != 0 && processId <= int.MaxValue && processIds.Contains((int)processId) &&
                NativeMethods.IsWindowVisible(candidate) &&
                NativeMethods.GetWindow(candidate, NativeMethods.GW_OWNER) == IntPtr.Zero)
            {
                candidates.Add(candidate);
            }

            return true;
        }, IntPtr.Zero);

        if (!enumerated)
            return ExecutionResult.Failure(new Win32Exception().Message);

        if (candidates.Count == 0)
            return ExecutionResult.Unavailable("No visible top-level helper window is available yet.");

        if (candidates.Count > 1)
            return ExecutionResult.Failure($"The helper has {candidates.Count} eligible top-level windows; the target is ambiguous.");

        window = candidates[0];
        return ExecutionResult.Success("Helper window found.");
    }

    private static ExecutionResult MoveWindow(IntPtr window, StartupMacroActionConfig action)
    {
        if (action.X is not int x || action.Y is not int y)
            return ExecutionResult.Failure("Both X and Y coordinates are required.");

        Screen? screen = ResolveScreen(action.Monitor);
        if (screen is null)
            return ExecutionResult.Failure($"Monitor '{action.Monitor}' is not connected.");

        int targetX = screen.WorkingArea.X + x;
        int targetY = screen.WorkingArea.Y + y;

        if (!NativeMethods.GetWindowRect(window, out NativeMethods.RECT rectangle))
            return ExecutionResult.Failure(new Win32Exception().Message);

        if (rectangle.Left == targetX && rectangle.Top == targetY)
        {
            return ExecutionResult.Success(
                $"Window remains at {screen.DeviceName} position {x}, {y}."
            );
        }

        bool moved = NativeMethods.SetWindowPos(
            window,
            IntPtr.Zero,
            targetX,
            targetY,
            0,
            0,
            NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOZORDER |
                NativeMethods.SWP_NOACTIVATE
        );

        return moved
            ? ExecutionResult.Adjusted($"Window moved to {screen.DeviceName} at {x}, {y}.")
            : ExecutionResult.Failure(new Win32Exception().Message);
    }

    private static ExecutionResult ResizeWindow(IntPtr window, StartupMacroActionConfig action)
    {
        if (action.Width is not int width || action.Height is not int height ||
            width <= 0 || height <= 0)
        {
            return ExecutionResult.Failure("Positive width and height values are required.");
        }

        if (!NativeMethods.GetWindowRect(window, out NativeMethods.RECT rectangle))
            return ExecutionResult.Failure(new Win32Exception().Message);

        if (rectangle.Right - rectangle.Left == width &&
            rectangle.Bottom - rectangle.Top == height)
        {
            return ExecutionResult.Success($"Window remains at {width} x {height}.");
        }

        bool resized = NativeMethods.SetWindowPos(
            window,
            IntPtr.Zero,
            0,
            0,
            width,
            height,
            NativeMethods.SWP_NOMOVE |
                NativeMethods.SWP_NOZORDER |
                NativeMethods.SWP_NOACTIVATE
        );

        return resized
            ? ExecutionResult.Adjusted($"Window resized to {width} x {height}.")
            : ExecutionResult.Failure(new Win32Exception().Message);
    }

    private static ExecutionResult ChangeWindowState(IntPtr window, uint command, string description)
    {
        bool queued = NativeMethods.PostMessage(
            window,
            NativeMethods.WM_SYSCOMMAND,
            new IntPtr((long)command),
            IntPtr.Zero
        );

        return queued
            ? ExecutionResult.Success($"Queued window {description} without activation.")
            : ExecutionResult.Failure(new Win32Exception().Message);
    }

    private static ExecutionResult BringToFront(IntPtr window)
    {
        bool reordered = NativeMethods.SetWindowPos(
            window,
            NativeMethods.HWND_TOP,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE |
                NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOACTIVATE
        );

        return reordered
            ? ExecutionResult.Success("Window moved to the front without activation.")
            : ExecutionResult.Failure(new Win32Exception().Message);
    }

    private static ExecutionResult SendHotkey(IReadOnlyList<string> keyNames)
    {
        var keys = new List<Keys>();

        foreach (string keyName in keyNames)
        {
            if (!Enum.TryParse(keyName, ignoreCase: true, out Keys key) || key == Keys.None)
                return ExecutionResult.Failure($"Captured key '{keyName}' is invalid.");

            keys.Add(key);
        }

        keys = keys.OrderByDescending(IsModifierKey).ToList();
        NativeMethods.INPUT[] inputs =
        [
            .. keys.Select(key => CreateKeyboardInput(key, keyUp: false)),
            .. keys.AsEnumerable().Reverse().Select(key => CreateKeyboardInput(key, keyUp: true))
        ];
        uint sent = NativeMethods.SendInput(
            (uint)inputs.Length,
            inputs,
            Marshal.SizeOf<NativeMethods.INPUT>()
        );

        return sent == inputs.Length
            ? ExecutionResult.Success(
                $"Injected {FormatHotkey(keyNames)} globally without activating the helper window."
            )
            : ExecutionResult.Failure(new Win32Exception().Message);
    }

    private static NativeMethods.INPUT CreateKeyboardInput(Keys key, bool keyUp) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        data = new NativeMethods.INPUTUNION
        {
            keyboard = new NativeMethods.KEYBDINPUT
            {
                wVk = (ushort)((uint)key & 0xffff),
                dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0
            }
        }
    };

    private static Screen? ResolveScreen(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return Screen.PrimaryScreen;

        return Screen.AllScreens.FirstOrDefault(screen =>
            string.Equals(screen.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
    }
}
