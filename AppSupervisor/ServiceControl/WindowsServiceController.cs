using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AppSupervisor.ServiceControl;

/// <summary>
/// Provides direct, dependency-free access to one Windows service through the native Service Control Manager API.
/// </summary>
internal sealed class WindowsServiceController : IWindowsServiceController
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServicePauseContinue = 0x0040;
    private const uint RequiredServiceAccess =
        ServiceQueryConfig |
        ServiceChangeConfig |
        ServiceQueryStatus |
        ServiceStart |
        ServiceStop |
        ServicePauseContinue;

    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceControlContinue = 0x00000003;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ServiceDemandStart = 0x00000003;
    private const int ScStatusProcessInfo = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;

    private readonly string _serviceName;
    private readonly SafeServiceHandle _managerHandle;
    private readonly SafeServiceHandle _serviceHandle;
    private bool _disposed;

    /// <summary>
    /// Opens the Service Control Manager and the named service with every right AppSupervisor requires.
    /// </summary>
    /// <param name="serviceName">The internal Windows service name.</param>
    public WindowsServiceController(string serviceName)
    {
        _serviceName = serviceName;
        _managerHandle = OpenSCManager(null, null, ScManagerConnect);

        if (_managerHandle.IsInvalid)
            throw CreateWin32Exception("open the Windows Service Control Manager");

        _serviceHandle = OpenService(
            _managerHandle,
            serviceName,
            RequiredServiceAccess
        );

        if (_serviceHandle.IsInvalid)
        {
            int errorCode = Marshal.GetLastWin32Error();
            _managerHandle.Dispose();
            throw CreateWin32Exception("open the service with query, start, stop, and configuration rights", errorCode);
        }
    }

    /// <summary>
    /// Confirms configuration access and converts the service startup mode to Manual when it is not already Manual.
    /// </summary>
    public void EnsureManualStartAndRequiredAccess()
    {
        ThrowIfDisposed();

        uint currentStartType = QueryStartType();

        if (currentStartType == ServiceDemandStart)
            return;

        if (!ChangeServiceConfig(
            _serviceHandle,
            ServiceNoChange,
            ServiceDemandStart,
            ServiceNoChange,
            null,
            null,
            IntPtr.Zero,
            null,
            null,
            null,
            null))
        {
            throw CreateWin32Exception("change the service startup type to Manual");
        }
    }

    /// <summary>
    /// Reads the current state reported by the Service Control Manager.
    /// </summary>
    /// <returns>The service's current runtime state.</returns>
    public ServiceRuntimeState GetState()
    {
        ThrowIfDisposed();

        uint bufferSize = (uint)Marshal.SizeOf<ServiceStatusProcess>();

        if (!QueryServiceStatusEx(
            _serviceHandle,
            ScStatusProcessInfo,
            out ServiceStatusProcess status,
            bufferSize,
            out _))
        {
            throw CreateWin32Exception("query the service status");
        }

        return (ServiceRuntimeState)status.CurrentState;
    }

    /// <summary>
    /// Sends a start request unless Windows reports that the service is already running.
    /// </summary>
    public void Start()
    {
        ThrowIfDisposed();

        if (StartService(_serviceHandle, 0, IntPtr.Zero))
            return;

        int errorCode = Marshal.GetLastWin32Error();

        if (errorCode != ErrorServiceAlreadyRunning)
            throw CreateWin32Exception("start the service", errorCode);
    }

    /// <summary>
    /// Sends a graceful stop control unless Windows reports that the service is already stopped.
    /// </summary>
    public void Stop()
    {
        ThrowIfDisposed();

        if (ControlService(
            _serviceHandle,
            ServiceControlStop,
            out _))
        {
            return;
        }

        int errorCode = Marshal.GetLastWin32Error();

        if (errorCode != ErrorServiceNotActive)
            throw CreateWin32Exception("stop the service", errorCode);
    }

    /// <summary>
    /// Sends a continue control to a paused service.
    /// </summary>
    public void Continue()
    {
        ThrowIfDisposed();

        if (!ControlService(
            _serviceHandle,
            ServiceControlContinue,
            out _))
        {
            throw CreateWin32Exception("continue the paused service");
        }
    }

    /// <summary>
    /// Releases the native service and Service Control Manager handles.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _serviceHandle.Dispose();
        _managerHandle.Dispose();
    }

    /// <summary>
    /// Reads the service's configured startup type using the variable-sized QUERY_SERVICE_CONFIG structure.
    /// </summary>
    /// <returns>The native SERVICE_*_START value.</returns>
    private uint QueryStartType()
    {
        QueryServiceConfig(
            _serviceHandle,
            IntPtr.Zero,
            0,
            out uint bytesNeeded
        );

        int firstError = Marshal.GetLastWin32Error();

        if (bytesNeeded == 0 || firstError != ErrorInsufficientBuffer)
            throw CreateWin32Exception("query the service configuration", firstError);

        IntPtr buffer = Marshal.AllocHGlobal(checked((int)bytesNeeded));

        try
        {
            if (!QueryServiceConfig(
                _serviceHandle,
                buffer,
                bytesNeeded,
                out _))
            {
                throw CreateWin32Exception("query the service configuration");
            }

            var config = Marshal.PtrToStructure<QueryServiceConfigData>(buffer);
            return config.StartType;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Throws when a caller attempts to use the controller after disposal.
    /// </summary>
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    /// Creates an operation-specific exception from the thread's current native error code.
    /// </summary>
    /// <param name="operation">The service operation that failed.</param>
    /// <returns>A Win32 exception containing the service name and native error details.</returns>
    private Win32Exception CreateWin32Exception(string operation)
    {
        return CreateWin32Exception(operation, Marshal.GetLastWin32Error());
    }

    /// <summary>
    /// Creates an operation-specific exception from an explicitly captured native error code.
    /// </summary>
    /// <param name="operation">The service operation that failed.</param>
    /// <param name="errorCode">The native Windows error code.</param>
    /// <returns>A Win32 exception containing the service name and native error details.</returns>
    private Win32Exception CreateWin32Exception(string operation, int errorCode)
    {
        var nativeException = new Win32Exception(errorCode);
        return new Win32Exception(
            errorCode,
            $"Could not {operation} '{_serviceName}': {nativeException.Message}"
        );
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfigData
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public IntPtr BinaryPathName;
        public IntPtr LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies;
        public IntPtr ServiceStartName;
        public IntPtr DisplayName;
    }

    /// <summary>
    /// Owns a native Service Control Manager or service handle.
    /// </summary>
    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        /// <summary>
        /// Creates an empty safe handle for P/Invoke marshalling.
        /// </summary>
        private SafeServiceHandle()
            : base(ownsHandle: true)
        {
        }

        /// <summary>
        /// Closes the underlying native service handle.
        /// </summary>
        /// <returns><see langword="true"/> when Windows releases the handle.</returns>
        protected override bool ReleaseHandle()
        {
            return CloseServiceHandle(handle);
        }
    }

    /// <summary>
    /// Opens the local Windows Service Control Manager database.
    /// </summary>
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess
    );

    /// <summary>
    /// Opens a named service with the requested access rights.
    /// </summary>
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenService(
        SafeServiceHandle managerHandle,
        string serviceName,
        uint desiredAccess
    );

    /// <summary>
    /// Releases a Service Control Manager or service handle.
    /// </summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);

    /// <summary>
    /// Queries extended runtime status for a service.
    /// </summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        SafeServiceHandle serviceHandle,
        int infoLevel,
        out ServiceStatusProcess buffer,
        uint bufferSize,
        out uint bytesNeeded
    );

    /// <summary>
    /// Reads the persistent configuration for a service.
    /// </summary>
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(
        SafeServiceHandle serviceHandle,
        IntPtr queryServiceConfig,
        uint bufferSize,
        out uint bytesNeeded
    );

    /// <summary>
    /// Changes persistent service configuration values such as startup type.
    /// </summary>
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig(
        SafeServiceHandle serviceHandle,
        uint serviceType,
        uint startType,
        uint errorControl,
        string? binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password,
        string? displayName
    );

    /// <summary>
    /// Sends a start request to a service.
    /// </summary>
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(
        SafeServiceHandle serviceHandle,
        uint serviceArgumentCount,
        IntPtr serviceArguments
    );

    /// <summary>
    /// Sends a control code such as Stop or Continue to a service.
    /// </summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(
        SafeServiceHandle serviceHandle,
        uint control,
        out ServiceStatus serviceStatus
    );
}
