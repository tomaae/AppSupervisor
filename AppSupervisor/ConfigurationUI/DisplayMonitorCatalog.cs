using System.Runtime.InteropServices;

namespace AppSupervisor.ConfigurationUI;

/// <summary>Builds stable monitor choices with human-readable hardware names when Windows exposes them.</summary>
internal static class DisplayMonitorCatalog
{
    internal sealed record MonitorChoice(
        string DeviceName,
        string DisplayName,
        Rectangle WorkingArea,
        bool Primary);

    /// <summary>Returns each active screen once, preserving its stable GDI device identifier.</summary>
    internal static IReadOnlyList<MonitorChoice> Load()
    {
        return Screen.AllScreens.Select(screen =>
        {
            string? friendlyName = GetFriendlyName(screen.DeviceName);
            string identity = string.IsNullOrWhiteSpace(friendlyName)
                ? screen.DeviceName
                : $"{friendlyName} ({screen.DeviceName})";
            string displayName = screen.Primary ? $"{identity} — Primary" : identity;
            return new MonitorChoice(
                screen.DeviceName,
                displayName,
                screen.WorkingArea,
                screen.Primary
            );
        }).ToList();
    }

    /// <summary>Reads the physical monitor description associated with one GDI display name.</summary>
    internal static string? GetFriendlyName(string deviceName)
    {
        var monitor = new NativeMethods.DISPLAY_DEVICE
        {
            cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>()
        };

        if (!NativeMethods.EnumDisplayDevices(deviceName, 0, ref monitor, 0))
            return null;

        string name = monitor.DeviceString?.Trim() ?? "";
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
