using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AppSupervisor.ServiceControl;

/// <summary>
/// Discovers locally installed Win32 services and removes Microsoft or Windows-provided entries from editor choices.
/// </summary>
internal static class InstalledServiceCatalog
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerEnumerateService = 0x0004;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceAutoStart = 0x00000002;
    private const uint ServiceWin32 = 0x00000030;
    private const uint ServiceStateAll = 0x00000003;
    private const int ScEnumProcessInfo = 0;
    private const int ErrorMoreData = 234;
    private const int ErrorInsufficientBuffer = 122;
    private const int EnumerationBufferSize = 256 * 1024;
    private const uint InfStyleWin4 = 0x00000002;

    private static readonly HashSet<string> WindowsServiceHosts = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "svchost.exe",
        "services.exe",
        "lsass.exe",
        "smss.exe",
        "wininit.exe"
    };

    /// <summary>
    /// Enumerates installed Win32 services and returns only choices that do not appear to be provided by Microsoft or Windows.
    /// </summary>
    /// <returns>Third-party service entries ordered by display name and internal service name.</returns>
    public static IReadOnlyList<InstalledServiceInfo> LoadThirdPartyServices()
    {
        var services = new List<InstalledServiceInfo>();
        EnumerateServices((manager, buffer, servicesReturned) =>
            AddPageServices(manager, buffer, servicesReturned, services));

        return services
            .GroupBy(service => service.ServiceName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(service => service.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(service => service.ServiceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Enumerates the process IDs currently hosting one or more Win32 services.</summary>
    /// <returns>The unique nonzero process IDs reported by the Service Control Manager.</returns>
    public static IReadOnlySet<int> LoadRunningServiceProcessIds()
    {
        var processIds = new HashSet<int>();
        EnumerateServices((_, buffer, servicesReturned) =>
            AddPageProcessIds(buffer, servicesReturned, processIds));
        return processIds;
    }

    /// <summary>
    /// Extracts and resolves the executable portion of a service command line.
    /// </summary>
    /// <param name="binaryPathName">The service's configured binary path and optional arguments.</param>
    /// <returns>The normalized executable path, or null when no executable can be identified.</returns>
    internal static string? ExtractExecutablePath(string? binaryPathName)
    {
        string value = binaryPathName?.Trim() ?? "";

        if (value.Length == 0)
            return null;

        string executable;

        if (value[0] == '"')
        {
            int closingQuote = value.IndexOf('"', 1);

            if (closingQuote < 0)
                return null;

            executable = value[1..closingQuote];
        }
        else
        {
            int extensionEnd = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);

            if (extensionEnd < 0)
                return null;

            executable = value[..(extensionEnd + 4)];
        }

        executable = Environment.ExpandEnvironmentVariables(executable.Trim());
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        if (executable.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            executable = Path.Combine(windowsDirectory, executable[12..]);

        if (executable.StartsWith(@"\??\", StringComparison.Ordinal))
            executable = executable[4..];
        else if (executable.StartsWith(@"\\?\", StringComparison.Ordinal))
            executable = executable[4..];

        try
        {
            return Path.GetFullPath(executable);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Determines whether executable metadata identifies a service as Microsoft or Windows-provided.
    /// </summary>
    /// <param name="binaryPathName">The original configured service command line.</param>
    /// <param name="executablePath">The resolved executable path.</param>
    /// <param name="publisher">The executable company name.</param>
    /// <returns>True when the service should be hidden from the third-party picker.</returns>
    internal static bool IsMicrosoftOrWindowsService(
        string binaryPathName,
        string executablePath,
        string? publisher)
    {
        if (publisher?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        if (WindowsServiceHosts.Contains(Path.GetFileName(executablePath)))
            return true;

        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string normalizedWindowsDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(windowsDirectory)
        ) + Path.DirectorySeparatorChar;
        bool hostedByWindowsPath = executablePath.StartsWith(
            normalizedWindowsDirectory,
            StringComparison.OrdinalIgnoreCase
        );
        bool configuredWithWindowsAlias =
            binaryPathName.Contains("%SystemRoot%", StringComparison.OrdinalIgnoreCase) ||
            binaryPathName.Contains(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase);

        return string.IsNullOrWhiteSpace(publisher) &&
            (hostedByWindowsPath || configuredWithWindowsAlias);
    }

    /// <summary>
    /// Uses executable version metadata first, then resolves a missing publisher from the containing DriverStore package INF.
    /// </summary>
    /// <param name="executablePublisher">The company name read from executable version metadata.</param>
    /// <param name="executablePath">The fully qualified service executable path.</param>
    /// <param name="driverStoreDirectory">The DriverStore FileRepository root.</param>
    /// <returns>The best available publisher name, or null when none can be determined.</returns>
    internal static string? ResolvePublisher(
        string? executablePublisher,
        string executablePath,
        string driverStoreDirectory)
    {
        if (!string.IsNullOrWhiteSpace(executablePublisher))
            return executablePublisher.Trim();

        try
        {
            string fullExecutablePath = Path.GetFullPath(executablePath);
            string fullDriverStoreDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(driverStoreDirectory)
            );
            string driverStorePrefix =
                fullDriverStoreDirectory + Path.DirectorySeparatorChar;

            if (!fullExecutablePath.StartsWith(
                driverStorePrefix,
                StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string relativePath = fullExecutablePath[driverStorePrefix.Length..];
            int separatorIndex = relativePath.IndexOfAny(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            );

            if (separatorIndex <= 0)
                return null;

            string packageDirectory = Path.Combine(
                fullDriverStoreDirectory,
                relativePath[..separatorIndex]
            );

            foreach (string infPath in Directory
                .GetFiles(packageDirectory, "*.inf", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string? provider = ReadInfProvider(infPath);

                if (!string.IsNullOrWhiteSpace(provider))
                    return provider.Trim();
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or
                UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>Reads the provider declared by an installed driver package INF.</summary>
    /// <param name="infPath">The fully qualified INF path.</param>
    /// <returns>The resolved Version-section provider, or null when it cannot be read.</returns>
    internal static string? ReadInfProvider(string infPath)
    {
        using SafeInfHandle inf = SetupOpenInfFile(
            infPath,
            infClass: null,
            InfStyleWin4,
            out _
        );

        if (inf.IsInvalid ||
            !SetupFindFirstLine(inf, "Version", "Provider", out InfContext context))
            return null;

        SetupGetStringField(ref context, 1, null, 0, out uint requiredSize);

        if (requiredSize <= 1 || requiredSize > int.MaxValue)
            return null;

        var provider = new StringBuilder(checked((int)requiredSize));
        return SetupGetStringField(
            ref context,
            1,
            provider,
            requiredSize,
            out _
        )
            ? provider.ToString()
            : null;
    }

    /// <summary>Enumerates every Win32 service page through one shared Service Control Manager handle.</summary>
    /// <param name="addPage">Consumes the native records returned for one enumeration page.</param>
    private static void EnumerateServices(
        Action<SafeServiceHandle, IntPtr, uint> addPage)
    {
        using SafeServiceHandle manager = OpenSCManager(
            null,
            null,
            ScManagerConnect | ScManagerEnumerateService
        );

        if (manager.IsInvalid)
            throw CreateWin32Exception("open the Windows Service Control Manager for enumeration");

        IntPtr buffer = Marshal.AllocHGlobal(EnumerationBufferSize);

        try
        {
            uint resumeHandle = 0;

            do
            {
                bool complete = EnumServicesStatusEx(
                    manager,
                    ScEnumProcessInfo,
                    ServiceWin32,
                    ServiceStateAll,
                    buffer,
                    EnumerationBufferSize,
                    out _,
                    out uint servicesReturned,
                    ref resumeHandle,
                    null
                );
                int errorCode = Marshal.GetLastWin32Error();

                if (!complete && errorCode != ErrorMoreData)
                    throw CreateWin32Exception("enumerate installed Windows services", errorCode);

                addPage(manager, buffer, servicesReturned);

                if (complete)
                    break;
            }
            while (resumeHandle != 0);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Adds the active service-host process IDs from one native enumeration page.</summary>
    /// <param name="buffer">The first native service-status record.</param>
    /// <param name="servicesReturned">The number of records in the page.</param>
    /// <param name="processIds">The destination process-ID set.</param>
    private static void AddPageProcessIds(
        IntPtr buffer,
        uint servicesReturned,
        ISet<int> processIds)
    {
        int recordSize = Marshal.SizeOf<EnumServiceStatusProcess>();

        for (uint index = 0; index < servicesReturned; index++)
        {
            IntPtr recordPointer = IntPtr.Add(buffer, checked((int)index * recordSize));
            EnumServiceStatusProcess record =
                Marshal.PtrToStructure<EnumServiceStatusProcess>(recordPointer);
            uint processId = record.ServiceStatusProcess.ProcessId;

            if (processId > 0 && processId <= int.MaxValue)
                processIds.Add((int)processId);
        }
    }

    /// <summary>
    /// Converts one native enumeration page into filtered managed service entries.
    /// </summary>
    /// <param name="manager">The open Service Control Manager handle.</param>
    /// <param name="buffer">The native page buffer.</param>
    /// <param name="servicesReturned">The number of records in the page.</param>
    /// <param name="services">The destination collection.</param>
    private static void AddPageServices(
        SafeServiceHandle manager,
        IntPtr buffer,
        uint servicesReturned,
        ICollection<InstalledServiceInfo> services)
    {
        int recordSize = Marshal.SizeOf<EnumServiceStatusProcess>();

        for (uint index = 0; index < servicesReturned; index++)
        {
            IntPtr recordPointer = IntPtr.Add(buffer, checked((int)index * recordSize));
            EnumServiceStatusProcess record =
                Marshal.PtrToStructure<EnumServiceStatusProcess>(recordPointer);
            string serviceName = Marshal.PtrToStringUni(record.ServiceName) ?? "";
            string displayName = Marshal.PtrToStringUni(record.DisplayName) ?? serviceName;

            if (serviceName.Length == 0 ||
                !TryReadBinaryPath(
                    manager,
                    serviceName,
                    out string binaryPathName,
                    out bool isAutomaticStart))
            {
                continue;
            }

            string? executablePath = ExtractExecutablePath(binaryPathName);

            if (executablePath is null)
                continue;

            string? publisher = ReadPublisher(executablePath);

            if (IsMicrosoftOrWindowsService(binaryPathName, executablePath, publisher))
                continue;

            services.Add(new InstalledServiceInfo(
                serviceName,
                string.IsNullOrWhiteSpace(displayName) ? serviceName : displayName,
                executablePath,
                publisher,
                isAutomaticStart
            ));
        }
    }

    /// <summary>
    /// Reads the configured binary command line for one installed service.
    /// </summary>
    /// <param name="manager">The open Service Control Manager handle.</param>
    /// <param name="serviceName">The internal service name.</param>
    /// <param name="binaryPathName">Receives the configured executable command line.</param>
    /// <param name="isAutomaticStart">Receives whether the service startup type is Automatic.</param>
    /// <returns>True when service configuration was readable and contained a binary path.</returns>
    private static bool TryReadBinaryPath(
        SafeServiceHandle manager,
        string serviceName,
        out string binaryPathName,
        out bool isAutomaticStart)
    {
        binaryPathName = "";
        isAutomaticStart = false;
        using SafeServiceHandle service = OpenService(
            manager,
            serviceName,
            ServiceQueryConfig
        );

        if (service.IsInvalid)
            return false;

        QueryServiceConfig(service, IntPtr.Zero, 0, out uint bytesNeeded);

        if (bytesNeeded == 0 || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
            return false;

        IntPtr buffer = Marshal.AllocHGlobal(checked((int)bytesNeeded));

        try
        {
            if (!QueryServiceConfig(service, buffer, bytesNeeded, out _))
                return false;

            QueryServiceConfigData config =
                Marshal.PtrToStructure<QueryServiceConfigData>(buffer);
            binaryPathName = Marshal.PtrToStringUni(config.BinaryPathName) ?? "";
            isAutomaticStart = config.StartType == ServiceAutoStart;
            return binaryPathName.Length > 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Reads the executable company name without allowing missing or malformed files to abort catalog discovery.
    /// </summary>
    /// <param name="executablePath">The resolved executable path.</param>
    /// <returns>The company name, or null when version metadata is unavailable.</returns>
    private static string? ReadPublisher(string executablePath)
    {
        string? executablePublisher = null;

        try
        {
            executablePublisher = FileVersionInfo.GetVersionInfo(executablePath).CompanyName;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
        }

        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string driverStoreDirectory = Path.Combine(
            windowsDirectory,
            "System32",
            "DriverStore",
            "FileRepository"
        );
        return ResolvePublisher(
            executablePublisher,
            executablePath,
            driverStoreDirectory
        );
    }

    /// <summary>
    /// Creates a descriptive exception from the calling thread's current native error code.
    /// </summary>
    /// <param name="operation">The catalog operation that failed.</param>
    /// <returns>A Win32 exception describing the failure.</returns>
    private static Win32Exception CreateWin32Exception(string operation)
    {
        return CreateWin32Exception(operation, Marshal.GetLastWin32Error());
    }

    /// <summary>
    /// Creates a descriptive exception from a captured native error code.
    /// </summary>
    /// <param name="operation">The catalog operation that failed.</param>
    /// <param name="errorCode">The native Windows error code.</param>
    /// <returns>A Win32 exception describing the failure.</returns>
    private static Win32Exception CreateWin32Exception(string operation, int errorCode)
    {
        var nativeException = new Win32Exception(errorCode);
        return new Win32Exception(
            errorCode,
            $"Could not {operation}: {nativeException.Message}"
        );
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
    private struct EnumServiceStatusProcess
    {
        public IntPtr ServiceName;
        public IntPtr DisplayName;
        public ServiceStatusProcess ServiceStatusProcess;
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

    /// <summary>Stores SetupAPI's opaque cursor for one parsed INF line.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct InfContext
    {
        public IntPtr Inf;
        public IntPtr CurrentInf;
        public uint Section;
        public uint Line;
    }

    /// <summary>Owns a native Service Control Manager or service handle.</summary>
    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        /// <summary>Creates an empty safe handle for native marshalling.</summary>
        private SafeServiceHandle()
            : base(ownsHandle: true)
        {
        }

        /// <summary>Releases the underlying native service handle.</summary>
        /// <returns>True when Windows releases the handle.</returns>
        protected override bool ReleaseHandle()
        {
            return CloseServiceHandle(handle);
        }
    }

    /// <summary>Owns a parsed INF handle returned by SetupAPI.</summary>
    private sealed class SafeInfHandle : SafeHandleMinusOneIsInvalid
    {
        /// <summary>Creates an empty safe INF handle for native marshalling.</summary>
        private SafeInfHandle()
            : base(ownsHandle: true)
        {
        }

        /// <summary>Releases SetupAPI resources associated with the parsed INF.</summary>
        /// <returns>True after the handle is released.</returns>
        protected override bool ReleaseHandle()
        {
            SetupCloseInfFile(handle);
            return true;
        }
    }

    /// <summary>Opens the local Windows Service Control Manager database.</summary>
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess
    );

    /// <summary>Enumerates a page of Win32 services and their current process status.</summary>
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusEx(
        SafeServiceHandle managerHandle,
        int infoLevel,
        uint serviceType,
        uint serviceState,
        IntPtr services,
        int bufferSize,
        out uint bytesNeeded,
        out uint servicesReturned,
        ref uint resumeHandle,
        string? groupName
    );

    /// <summary>Opens one named service for configuration queries.</summary>
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenService(
        SafeServiceHandle managerHandle,
        string serviceName,
        uint desiredAccess
    );

    /// <summary>Reads the persistent configuration for one service.</summary>
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(
        SafeServiceHandle serviceHandle,
        IntPtr queryServiceConfig,
        uint bufferSize,
        out uint bytesNeeded
    );

    /// <summary>Opens and parses an installed driver package INF.</summary>
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeInfHandle SetupOpenInfFile(
        string fileName,
        string? infClass,
        uint infStyle,
        out uint errorLine
    );

    /// <summary>Locates a keyed line within an INF section.</summary>
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupFindFirstLine(
        SafeInfHandle infHandle,
        string section,
        string key,
        out InfContext context
    );

    /// <summary>Reads one resolved string field from an INF line.</summary>
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupGetStringField(
        ref InfContext context,
        uint fieldIndex,
        StringBuilder? returnBuffer,
        uint returnBufferSize,
        out uint requiredSize
    );

    /// <summary>Closes a parsed INF handle.</summary>
    [DllImport("setupapi.dll")]
    private static extern void SetupCloseInfFile(IntPtr infHandle);

    /// <summary>Releases a Service Control Manager or service handle.</summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);
}
