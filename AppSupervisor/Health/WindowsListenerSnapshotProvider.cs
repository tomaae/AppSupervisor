using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AppSupervisor.Health;

/// <summary>
/// Supplies short-lived immutable snapshots of Windows TCP and UDP listener ownership tables.
/// </summary>
internal interface IListenerSnapshotProvider
{
    /// <summary>Gets the current shared listener snapshot.</summary>
    /// <returns>A snapshot containing IPv4 and IPv6 TCP listeners and UDP bindings.</returns>
    ListenerSnapshot GetSnapshot();
}

/// <summary>
/// Stores listener protocol, local port, and owner process identifiers without retaining address-specific state.
/// </summary>
internal sealed class ListenerSnapshot
{
    private readonly HashSet<ListenerEndpoint> _endpoints;

    /// <summary>Creates an immutable lookup from native listener rows.</summary>
    /// <param name="endpoints">The discovered protocol, port, and process combinations.</param>
    public ListenerSnapshot(IEnumerable<ListenerEndpoint> endpoints)
    {
        _endpoints = endpoints.ToHashSet();
    }

    /// <summary>Checks whether any supplied helper process owns the requested listener.</summary>
    /// <param name="protocol">The required TCP or UDP protocol.</param>
    /// <param name="port">The required local port.</param>
    /// <param name="ownerProcessIds">The acceptable owner process identifiers.</param>
    /// <returns>True when a matching native listener row exists.</returns>
    public bool Contains(
        ListenerProtocol protocol,
        int port,
        IReadOnlySet<int> ownerProcessIds)
    {
        return ownerProcessIds.Any(processId =>
            _endpoints.Contains(new ListenerEndpoint(protocol, port, processId)));
    }
}

/// <summary>Identifies one address-independent listener owner.</summary>
/// <param name="Protocol">The TCP or UDP transport.</param>
/// <param name="Port">The local port.</param>
/// <param name="ProcessId">The owning Windows process identifier.</param>
internal readonly record struct ListenerEndpoint(
    ListenerProtocol Protocol,
    int Port,
    int ProcessId);

/// <summary>
/// Queries native Windows listener tables and shares one result across checks running in the same polling interval.
/// </summary>
internal sealed class WindowsListenerSnapshotProvider : IListenerSnapshotProvider
{
    private const int AddressFamilyInterNetwork = 2;
    private const int AddressFamilyInterNetworkV6 = 23;
    private const uint ErrorSuccess = 0;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint TcpTableOwnerPidListener = 3;
    private const uint UdpTableOwnerPid = 1;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(1);
    private readonly object _syncRoot = new();
    private ListenerSnapshot? _cachedSnapshot;
    private DateTime _cacheExpiresUtc;

    /// <summary>Gets the process-wide shared native listener snapshot provider.</summary>
    public static WindowsListenerSnapshotProvider Instance { get; } = new();

    /// <summary>Prevents additional provider instances outside tests and the process-wide singleton.</summary>
    private WindowsListenerSnapshotProvider()
    {
    }

    /// <summary>Returns a cached snapshot or atomically refreshes all four native ownership tables.</summary>
    /// <returns>A current address-independent listener ownership snapshot.</returns>
    public ListenerSnapshot GetSnapshot()
    {
        lock (_syncRoot)
        {
            DateTime nowUtc = DateTime.UtcNow;

            if (_cachedSnapshot is not null && nowUtc < _cacheExpiresUtc)
                return _cachedSnapshot;

            _cachedSnapshot = ReadSnapshot();
            _cacheExpiresUtc = nowUtc + CacheDuration;
            return _cachedSnapshot;
        }
    }

    /// <summary>Reads IPv4 and IPv6 TCP listener and UDP owner rows into one immutable snapshot.</summary>
    /// <returns>The newly captured listener snapshot.</returns>
    private static ListenerSnapshot ReadSnapshot()
    {
        var endpoints = new HashSet<ListenerEndpoint>();
        AddTcp4Endpoints(endpoints);
        AddTcp6Endpoints(endpoints);
        AddUdp4Endpoints(endpoints);
        AddUdp6Endpoints(endpoints);
        return new ListenerSnapshot(endpoints);
    }

    /// <summary>Adds IPv4 TCP listener owners.</summary>
    /// <param name="endpoints">The destination endpoint set.</param>
    private static void AddTcp4Endpoints(ICollection<ListenerEndpoint> endpoints)
    {
        ReadTable<Tcp4Row>(
            GetExtendedTcpTable,
            AddressFamilyInterNetwork,
            TcpTableOwnerPidListener,
            row => endpoints.Add(new ListenerEndpoint(
                ListenerProtocol.Tcp,
                DecodePort(row.LocalPort),
                checked((int)row.OwningProcessId)
            ))
        );
    }

    /// <summary>Adds IPv6 TCP listener owners.</summary>
    /// <param name="endpoints">The destination endpoint set.</param>
    private static void AddTcp6Endpoints(ICollection<ListenerEndpoint> endpoints)
    {
        ReadTable<Tcp6Row>(
            GetExtendedTcpTable,
            AddressFamilyInterNetworkV6,
            TcpTableOwnerPidListener,
            row => endpoints.Add(new ListenerEndpoint(
                ListenerProtocol.Tcp,
                DecodePort(row.LocalPort),
                checked((int)row.OwningProcessId)
            ))
        );
    }

    /// <summary>Adds IPv4 UDP binding owners.</summary>
    /// <param name="endpoints">The destination endpoint set.</param>
    private static void AddUdp4Endpoints(ICollection<ListenerEndpoint> endpoints)
    {
        ReadTable<Udp4Row>(
            GetExtendedUdpTable,
            AddressFamilyInterNetwork,
            UdpTableOwnerPid,
            row => endpoints.Add(new ListenerEndpoint(
                ListenerProtocol.Udp,
                DecodePort(row.LocalPort),
                checked((int)row.OwningProcessId)
            ))
        );
    }

    /// <summary>Adds IPv6 UDP binding owners.</summary>
    /// <param name="endpoints">The destination endpoint set.</param>
    private static void AddUdp6Endpoints(ICollection<ListenerEndpoint> endpoints)
    {
        ReadTable<Udp6Row>(
            GetExtendedUdpTable,
            AddressFamilyInterNetworkV6,
            UdpTableOwnerPid,
            row => endpoints.Add(new ListenerEndpoint(
                ListenerProtocol.Udp,
                DecodePort(row.LocalPort),
                checked((int)row.OwningProcessId)
            ))
        );
    }

    /// <summary>Reads one variable-sized native owner table and converts each row.</summary>
    /// <typeparam name="TRow">The native row structure.</typeparam>
    /// <param name="reader">The TCP or UDP native table function.</param>
    /// <param name="addressFamily">The IPv4 or IPv6 address family.</param>
    /// <param name="tableClass">The native owner-PID table class.</param>
    /// <param name="addRow">The row conversion callback.</param>
    private static void ReadTable<TRow>(
        NativeTableReader reader,
        int addressFamily,
        uint tableClass,
        Action<TRow> addRow)
        where TRow : struct
    {
        uint bufferSize = 0;
        uint result = reader(
            IntPtr.Zero,
            ref bufferSize,
            order: false,
            addressFamily,
            tableClass,
            reserved: 0
        );

        if (result != ErrorInsufficientBuffer || bufferSize == 0)
            throw CreateNativeException("measure a Windows listener table", result);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)bufferSize));

            try
            {
                result = reader(
                    buffer,
                    ref bufferSize,
                    order: false,
                    addressFamily,
                    tableClass,
                    reserved: 0
                );

                if (result == ErrorInsufficientBuffer)
                    continue;

                if (result != ErrorSuccess)
                    throw CreateNativeException("read a Windows listener table", result);

                uint rowCount = unchecked((uint)Marshal.ReadInt32(buffer));
                int rowSize = Marshal.SizeOf<TRow>();
                IntPtr rowPointer = IntPtr.Add(buffer, sizeof(uint));

                for (uint index = 0; index < rowCount; index++)
                {
                    TRow row = Marshal.PtrToStructure<TRow>(
                        IntPtr.Add(rowPointer, checked((int)index * rowSize))
                    );
                    addRow(row);
                }

                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new InvalidOperationException(
            "Windows listener tables changed repeatedly while AppSupervisor was reading them."
        );
    }

    /// <summary>Converts a native network-byte-order DWORD port to a host-order integer.</summary>
    /// <param name="nativePort">The native port value.</param>
    /// <returns>The local port number.</returns>
    private static int DecodePort(uint nativePort)
    {
        return checked((int)(
            ((nativePort & 0x000000FF) << 8) |
            ((nativePort & 0x0000FF00) >> 8)
        ));
    }

    /// <summary>Creates a readable Win32 exception from a native status code.</summary>
    /// <param name="operation">The listener-table operation that failed.</param>
    /// <param name="errorCode">The native Windows status code.</param>
    /// <returns>A descriptive Win32 exception.</returns>
    private static Win32Exception CreateNativeException(string operation, uint errorCode)
    {
        var nativeException = new Win32Exception(checked((int)errorCode));
        return new Win32Exception(
            checked((int)errorCode),
            $"Could not {operation}: {nativeException.Message}"
        );
    }

    /// <summary>Represents the common GetExtendedTcpTable/GetExtendedUdpTable signature.</summary>
    private delegate uint NativeTableReader(
        IntPtr table,
        ref uint bufferSize,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        uint tableClass,
        uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct Tcp4Row
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Tcp6Row
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddress;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddress;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Udp4Row
    {
        public uint LocalAddress;
        public uint LocalPort;
        public uint OwningProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Udp6Row
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddress;
        public uint LocalScopeId;
        public uint LocalPort;
        public uint OwningProcessId;
    }

    /// <summary>Reads an extended TCP ownership table.</summary>
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref uint bufferSize,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        uint tableClass,
        uint reserved);

    /// <summary>Reads an extended UDP ownership table.</summary>
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr udpTable,
        ref uint bufferSize,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        uint tableClass,
        uint reserved);
}
