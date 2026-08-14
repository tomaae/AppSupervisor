using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AppSupervisor.SteamVr;

/// <summary>
/// Attaches as an OpenVR background client inside the isolated capture host and reads tracked-device state.
/// </summary>
internal sealed class OpenVrDeviceSource : ISteamVrDeviceSource
{
    private const int BackgroundApplicationType = 3;
    private const int NoServerForBackgroundApp = 121;
    private const string SystemInterfaceVersion = "FnTable:IVRSystem_026";
    private const int MaximumTrackedDeviceCount = 64;
    private const int ControllerClass = 2;
    private const int GenericTrackerClass = 3;
    private const int TrackingReferenceClass = 4;
    private const int TrackingSystemNameProperty = 1000;
    private const int ModelNumberProperty = 1001;
    private const int SerialNumberProperty = 1002;
    private const string SettingsInterfaceVersion = "FnTable:IVRSettings_003";
    private const string TrackersSettingsSection = "trackers";

    private readonly object _sync = new();
    private IntPtr _library;
    private string? _libraryPath;
    private int _connectedProcessId;
    private bool _openVrInitialized;
    private VrInitInternalDelegate? _initialize;
    private VrShutdownInternalDelegate? _shutdown;
    private VrGetGenericInterfaceDelegate? _getInterface;
    private VrGetErrorDescriptionDelegate? _getErrorDescription;
    private GetTrackedDeviceClassDelegate? _getDeviceClass;
    private GetControllerRoleForTrackedDeviceIndexDelegate? _getControllerRole;
    private IsTrackedDeviceConnectedDelegate? _isDeviceConnected;
    private GetStringTrackedDevicePropertyDelegate? _getStringProperty;
    private GetSettingsStringDelegate? _getSettingsString;
    private bool _disposed;

    /// <summary>Captures supported device state without starting SteamVR when it is absent.</summary>
    public SteamVrSnapshot Capture()
    {
        lock (_sync)
        {
            if (_disposed)
                return new SteamVrSnapshot(false, null, [], "The SteamVR device source is disposed.");

            RunningVrServer? server = FindRunningVrServer();

            if (server is null)
            {
                DisconnectServer();
                return new SteamVrSnapshot(false, null, []);
            }

            try
            {
                EnsureConnected(server);
                return new SteamVrSnapshot(
                    true,
                    server.StartedUtc,
                    EnumerateDevices()
                );
            }
            catch (OpenVrUnavailableException ex)
            {
                if (ex.ErrorCode == NoServerForBackgroundApp)
                {
                    DisconnectServer();
                    return new SteamVrSnapshot(false, null, []);
                }

                DisconnectServer();
                return new SteamVrSnapshot(true, server.StartedUtc, [], ex.Message);
            }
            catch (Exception ex)
            {
                DisconnectServer();
                return new SteamVrSnapshot(true, server.StartedUtc, [], ex.Message);
            }
        }
    }

    /// <summary>Shuts down the OpenVR client and unloads its runtime library.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            DisconnectServer();

            if (_library != IntPtr.Zero)
                NativeLibrary.Free(_library);

            _library = IntPtr.Zero;
            _libraryPath = null;
        }
    }

    private void EnsureConnected(RunningVrServer server)
    {
        string dllPath = Path.Combine(
            Path.GetDirectoryName(server.ExecutablePath)
                ?? throw new OpenVrUnavailableException("SteamVR has no runtime directory."),
            "openvr_api.dll"
        );

        if (!File.Exists(dllPath))
            throw new OpenVrUnavailableException($"OpenVR runtime library was not found: {dllPath}");

        if (!string.Equals(_libraryPath, dllPath, StringComparison.OrdinalIgnoreCase))
        {
            DisconnectServer();

            if (_library != IntPtr.Zero)
                NativeLibrary.Free(_library);

            _library = NativeLibrary.Load(dllPath);
            _libraryPath = dllPath;
            BindExports();
        }

        if (_connectedProcessId == server.ProcessId && _openVrInitialized)
            return;

        DisconnectServer();
        int error = 0;
        _ = _initialize!(ref error, BackgroundApplicationType);

        if (error != 0)
            throw new OpenVrUnavailableException(DescribeError(error), error);

        _openVrInitialized = true;
        _connectedProcessId = server.ProcessId;
        error = 0;
        IntPtr table = _getInterface!(SystemInterfaceVersion, ref error);

        if (table == IntPtr.Zero || error != 0)
            throw new OpenVrUnavailableException(DescribeError(error), error);

        _getDeviceClass = BindFunction<GetTrackedDeviceClassDelegate>(table, 20);
        _getControllerRole = BindFunction<GetControllerRoleForTrackedDeviceIndexDelegate>(table, 19);
        _isDeviceConnected = BindFunction<IsTrackedDeviceConnectedDelegate>(table, 21);
        _getStringProperty = BindFunction<GetStringTrackedDevicePropertyDelegate>(table, 28);

        error = 0;
        IntPtr settingsTable = _getInterface!(SettingsInterfaceVersion, ref error);

        if (settingsTable != IntPtr.Zero && error == 0)
            _getSettingsString = BindFunction<GetSettingsStringDelegate>(settingsTable, 9);
    }

    private IReadOnlyList<SteamVrDeviceSnapshot> EnumerateDevices()
    {
        var devices = new List<SteamVrDeviceSnapshot>();

        for (uint index = 0; index < MaximumTrackedDeviceCount; index++)
        {
            int nativeClass = _getDeviceClass!(index);
            SteamVrDeviceClass? deviceClass = nativeClass switch
            {
                ControllerClass => SteamVrDeviceClass.Controller,
                GenericTrackerClass => SteamVrDeviceClass.GenericTracker,
                TrackingReferenceClass => SteamVrDeviceClass.TrackingReference,
                _ => null
            };

            if (deviceClass is null)
                continue;

            string serial = ReadStringProperty(index, SerialNumberProperty);

            if (string.IsNullOrWhiteSpace(serial))
                continue;

            devices.Add(new SteamVrDeviceSnapshot(
                serial,
                ReadStringProperty(index, ModelNumberProperty),
                deviceClass.Value,
                _isDeviceConnected!(index) != 0,
                ReadDeviceRole(index, serial, deviceClass.Value)
            ));
        }

        return devices
            .GroupBy(device => device.SerialNumber, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(device => device.DeviceClass)
            .ThenBy(device => device.SerialNumber, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Reads the runtime-selected controller role or user-selected tracker role.</summary>
    private SteamVrDeviceRole ReadDeviceRole(
        uint deviceIndex,
        string serial,
        SteamVrDeviceClass deviceClass)
    {
        if (deviceClass == SteamVrDeviceClass.Controller)
            return MapControllerRole(_getControllerRole?.Invoke(deviceIndex) ?? 0);

        if (deviceClass != SteamVrDeviceClass.GenericTracker || _getSettingsString is null)
            return SteamVrDeviceRole.None;

        string trackingSystem = ReadStringProperty(deviceIndex, TrackingSystemNameProperty);

        if (string.IsNullOrWhiteSpace(trackingSystem))
            return SteamVrDeviceRole.None;

        string settingsKey = BuildTrackerSettingsKey(trackingSystem, serial);
        IntPtr buffer = Marshal.AllocHGlobal(256);

        try
        {
            Marshal.WriteByte(buffer, 0);
            int error = 0;
            _getSettingsString(TrackersSettingsSection, settingsKey, buffer, 256, ref error);
            string role = error == 0 ? Marshal.PtrToStringUTF8(buffer) ?? "" : "";
            return MapTrackerRole(role);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static string BuildTrackerSettingsKey(string trackingSystem, string serial)
        => $"/devices/{trackingSystem}/{serial}";

    internal static SteamVrDeviceRole MapControllerRole(int nativeRole) => nativeRole switch
    {
        1 => SteamVrDeviceRole.LeftHand,
        2 => SteamVrDeviceRole.RightHand,
        3 => SteamVrDeviceRole.OptOut,
        4 => SteamVrDeviceRole.Treadmill,
        5 => SteamVrDeviceRole.Stylus,
        _ => SteamVrDeviceRole.None
    };

    internal static SteamVrDeviceRole MapTrackerRole(string role) => role switch
    {
        "TrackerRole_Handed" => SteamVrDeviceRole.Handed,
        "TrackerRole_LeftFoot" => SteamVrDeviceRole.LeftFoot,
        "TrackerRole_RightFoot" => SteamVrDeviceRole.RightFoot,
        "TrackerRole_LeftShoulder" => SteamVrDeviceRole.LeftShoulder,
        "TrackerRole_RightShoulder" => SteamVrDeviceRole.RightShoulder,
        "TrackerRole_LeftElbow" => SteamVrDeviceRole.LeftElbow,
        "TrackerRole_RightElbow" => SteamVrDeviceRole.RightElbow,
        "TrackerRole_LeftKnee" => SteamVrDeviceRole.LeftKnee,
        "TrackerRole_RightKnee" => SteamVrDeviceRole.RightKnee,
        "TrackerRole_Waist" => SteamVrDeviceRole.Waist,
        "TrackerRole_Chest" => SteamVrDeviceRole.Chest,
        "TrackerRole_Camera" => SteamVrDeviceRole.Camera,
        "TrackerRole_Keyboard" => SteamVrDeviceRole.Keyboard,
        _ => SteamVrDeviceRole.None
    };

    private string ReadStringProperty(uint deviceIndex, int property)
    {
        int error = 0;
        uint required = _getStringProperty!(deviceIndex, property, IntPtr.Zero, 0, ref error);

        if (required <= 1)
            return "";

        IntPtr buffer = Marshal.AllocHGlobal(checked((int)required));

        try
        {
            error = 0;
            uint written = _getStringProperty(deviceIndex, property, buffer, required, ref error);
            return error == 0 && written > 1
                ? Marshal.PtrToStringUTF8(buffer, checked((int)written - 1)) ?? ""
                : "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void BindExports()
    {
        _initialize = BindExport<VrInitInternalDelegate>("VR_InitInternal");
        _shutdown = BindExport<VrShutdownInternalDelegate>("VR_ShutdownInternal");
        _getInterface = BindExport<VrGetGenericInterfaceDelegate>("VR_GetGenericInterface");
        _getErrorDescription = BindExport<VrGetErrorDescriptionDelegate>(
            "VR_GetVRInitErrorAsEnglishDescription"
        );
    }

    private T BindExport<T>(string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    private static T BindFunction<T>(IntPtr table, int functionIndex) where T : Delegate
    {
        IntPtr pointer = Marshal.ReadIntPtr(table, functionIndex * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    private string DescribeError(int error)
    {
        IntPtr description = _getErrorDescription?.Invoke(error) ?? IntPtr.Zero;
        string text = description == IntPtr.Zero
            ? "Unknown OpenVR initialization error"
            : Marshal.PtrToStringUTF8(description) ?? "Unknown OpenVR initialization error";
        return $"{text} ({error}).";
    }

    private void DisconnectServer()
    {
        if (_openVrInitialized)
        {
            try
            {
                _shutdown?.Invoke();
            }
            catch
            {
            }
        }

        _openVrInitialized = false;
        _connectedProcessId = 0;
        _getDeviceClass = null;
        _getControllerRole = null;
        _isDeviceConnected = null;
        _getStringProperty = null;
        _getSettingsString = null;
    }

    private static RunningVrServer? FindRunningVrServer()
    {
        Process[] processes = Process.GetProcessesByName("vrserver");

        try
        {
            foreach (Process process in processes)
            {
                try
                {
                    string? path = process.MainModule?.FileName;

                    if (!string.IsNullOrWhiteSpace(path))
                        return new RunningVrServer(process.Id, path, process.StartTime.ToUniversalTime());
                }
                catch
                {
                }
            }
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }

        return null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr VrInitInternalDelegate(ref int error, int applicationType);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VrShutdownInternalDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate IntPtr VrGetGenericInterfaceDelegate(string version, ref int error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr VrGetErrorDescriptionDelegate(int error);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetTrackedDeviceClassDelegate(uint deviceIndex);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetControllerRoleForTrackedDeviceIndexDelegate(uint deviceIndex);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate byte IsTrackedDeviceConnectedDelegate(uint deviceIndex);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint GetStringTrackedDevicePropertyDelegate(
        uint deviceIndex,
        int property,
        IntPtr value,
        uint bufferSize,
        ref int error);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi)]
    private delegate void GetSettingsStringDelegate(
        string section,
        string key,
        IntPtr value,
        uint bufferSize,
        ref int error);

    private sealed record RunningVrServer(int ProcessId, string ExecutablePath, DateTime StartedUtc);

    private sealed class OpenVrUnavailableException : Exception
    {
        public OpenVrUnavailableException(string message, int errorCode = 0)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public int ErrorCode { get; }
    }
}
