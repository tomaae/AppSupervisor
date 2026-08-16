using System.Runtime.InteropServices;

namespace AppSupervisor.WindowsAudio;

/// <summary>Discovers and controls active Windows Core Audio endpoints.</summary>
internal sealed class WindowsAudioController : IWindowsAudioController
{
    private static readonly Guid AudioEndpointVolumeInterfaceId =
        new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly PropertyKey DeviceFriendlyName = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        14
    );
    private static readonly PropertyKey DeviceInterfaceFriendlyName = new(
        new Guid("026E516E-B814-414B-83CD-856D6FEF4822"),
        2
    );
    private static readonly PropertyKey DeviceInstanceId = new(
        new Guid("78C34FC8-104A-4ACA-9EA4-524D52996E57"),
        256
    );
    private static readonly PropertyKey DeviceContainerId = new(
        new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"),
        2
    );

    public IReadOnlyList<AudioEndpointSnapshot> GetActiveEndpoints()
    {
        EnsureWindows();
        IMMDeviceEnumerator? enumerator = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            var endpoints = new List<AudioEndpointSnapshot>();
            TryAddDefaultEndpoint(
                enumerator,
                AudioDataFlow.Render,
                AudioInterfaceDirection.Output,
                endpoints
            );
            TryAddDefaultEndpoint(
                enumerator,
                AudioDataFlow.Capture,
                AudioInterfaceDirection.Input,
                endpoints
            );
            AddEndpoints(enumerator, AudioDataFlow.Render, AudioInterfaceDirection.Output, endpoints);
            AddEndpoints(enumerator, AudioDataFlow.Capture, AudioInterfaceDirection.Input, endpoints);
            return endpoints
                .OrderByDescending(endpoint => endpoint.FollowsDefault)
                .ThenBy(endpoint => endpoint.Direction)
                .ThenBy(endpoint => endpoint.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        finally
        {
            ReleaseComObject(enumerator);
        }
    }

    public AudioEndpointSnapshot ResolveEndpoint(AudioInterfaceResourceConfig configuration)
    {
        AudioEndpointSnapshot endpoint = configuration.UseDefaultDevice
            ? GetDefaultEndpoint(configuration.Direction)
            : WindowsAudioEndpointResolver.Resolve(configuration, GetActiveEndpoints());
        endpoint.CopyIdentityTo(configuration);
        return endpoint;
    }

    public AudioEndpointState GetState(string endpointId)
    {
        return WithEndpointVolume(endpointId, volume =>
        {
            ThrowIfFailed(volume.GetMasterVolumeLevelScalar(out float scalar));
            ThrowIfFailed(volume.GetMute(out int muted));
            return new AudioEndpointState(scalar, muted != 0);
        });
    }

    public void SetState(string endpointId, AudioEndpointState state)
    {
        WithEndpointVolume(endpointId, volume =>
        {
            Guid eventContext = Guid.Empty;
            ThrowIfFailed(volume.SetMasterVolumeLevelScalar(
                Math.Clamp(state.VolumeScalar, 0f, 1f),
                ref eventContext
            ));
            ThrowIfFailed(volume.SetMute(state.Muted ? 1 : 0, ref eventContext));
            return true;
        });
    }

    private static T WithEndpointVolume<T>(
        string endpointId,
        Func<IAudioEndpointVolume, T> operation)
    {
        EnsureWindows();
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? activated = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            ThrowIfFailed(enumerator.GetDevice(endpointId, out device));
            Guid interfaceId = AudioEndpointVolumeInterfaceId;
            ThrowIfFailed(device.Activate(
                ref interfaceId,
                ComClassContext.InProcessServer,
                IntPtr.Zero,
                out activated
            ));
            return operation((IAudioEndpointVolume)activated);
        }
        finally
        {
            ReleaseComObject(activated);
            ReleaseComObject(device);
            ReleaseComObject(enumerator);
        }
    }

    private static void AddEndpoints(
        IMMDeviceEnumerator enumerator,
        AudioDataFlow dataFlow,
        AudioInterfaceDirection direction,
        ICollection<AudioEndpointSnapshot> destination)
    {
        IMMDeviceCollection? collection = null;

        try
        {
            ThrowIfFailed(enumerator.EnumAudioEndpoints(
                dataFlow,
                DeviceState.Active,
                out collection
            ));
            ThrowIfFailed(collection.GetCount(out uint count));

            for (uint index = 0; index < count; index++)
            {
                IMMDevice? device = null;

                try
                {
                    ThrowIfFailed(collection.Item(index, out device));
                    destination.Add(ReadEndpoint(device, direction, followsDefault: false));
                }
                finally
                {
                    ReleaseComObject(device);
                }
            }
        }
        finally
        {
            ReleaseComObject(collection);
        }
    }

    private static void TryAddDefaultEndpoint(
        IMMDeviceEnumerator enumerator,
        AudioDataFlow dataFlow,
        AudioInterfaceDirection direction,
        ICollection<AudioEndpointSnapshot> destination)
    {
        IMMDevice? device = null;

        try
        {
            int result = enumerator.GetDefaultAudioEndpoint(
                dataFlow,
                AudioRole.Multimedia,
                out device
            );

            if (result >= 0)
                destination.Add(ReadEndpoint(device, direction, followsDefault: true));
        }
        finally
        {
            ReleaseComObject(device);
        }
    }

    private static AudioEndpointSnapshot GetDefaultEndpoint(AudioInterfaceDirection direction)
    {
        EnsureWindows();
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            AudioDataFlow dataFlow = direction == AudioInterfaceDirection.Output
                ? AudioDataFlow.Render
                : AudioDataFlow.Capture;
            ThrowIfFailed(enumerator.GetDefaultAudioEndpoint(
                dataFlow,
                AudioRole.Multimedia,
                out device
            ));
            return ReadEndpoint(device, direction, followsDefault: true);
        }
        finally
        {
            ReleaseComObject(device);
            ReleaseComObject(enumerator);
        }
    }

    private static AudioEndpointSnapshot ReadEndpoint(
        IMMDevice device,
        AudioInterfaceDirection direction,
        bool followsDefault)
    {
        IPropertyStore? properties = null;

        try
        {
            ThrowIfFailed(device.GetId(out string endpointId));
            ThrowIfFailed(device.OpenPropertyStore(StorageAccessMode.Read, out properties));
            string friendlyName = ReadString(properties, DeviceFriendlyName);
            return new AudioEndpointSnapshot(
                endpointId,
                ReadString(properties, DeviceInstanceId),
                ReadGuid(properties, DeviceContainerId),
                string.IsNullOrWhiteSpace(friendlyName) ? endpointId : friendlyName,
                ReadString(properties, DeviceInterfaceFriendlyName),
                direction,
                followsDefault
            );
        }
        finally
        {
            ReleaseComObject(properties);
        }
    }

    private static string ReadString(IPropertyStore properties, PropertyKey key)
    {
        var value = new PropVariant();

        try
        {
            ThrowIfFailed(properties.GetValue(ref key, out value));
            return value.GetString();
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    private static string ReadGuid(IPropertyStore properties, PropertyKey key)
    {
        var value = new PropVariant();

        try
        {
            ThrowIfFailed(properties.GetValue(ref key, out value));
            return value.GetGuid()?.ToString("D") ?? "";
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows audio interfaces require Windows.");
    }

    private static void ThrowIfFailed(int result) => Marshal.ThrowExceptionForHR(result);

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    private enum AudioDataFlow
    {
        Render,
        Capture,
        All
    }

    private enum AudioRole
    {
        Console,
        Multimedia,
        Communications
    }

    [Flags]
    private enum DeviceState : uint
    {
        Active = 0x1
    }

    [Flags]
    private enum ComClassContext : uint
    {
        InProcessServer = 0x1
    }

    private enum StorageAccessMode : uint
    {
        Read = 0
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    // PROPVARIANT's largest union member makes the native structure 24 bytes on x64.
    // Keeping the full buffer is important even though this class reads only strings and GUIDs.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        private readonly ushort _type;

        [FieldOffset(8)]
        private readonly IntPtr _pointer;

        public string GetString() => _type == 31 && _pointer != IntPtr.Zero
            ? Marshal.PtrToStringUni(_pointer) ?? ""
            : "";

        public Guid? GetGuid() => _type == 72 && _pointer != IntPtr.Zero
            ? Marshal.PtrToStructure<Guid>(_pointer)
            : null;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(
            AudioDataFlow dataFlow,
            DeviceState stateMask,
            out IMMDeviceCollection devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(
            AudioDataFlow dataFlow,
            AudioRole role,
            out IMMDevice device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid interfaceId,
            ComClassContext classContext,
            IntPtr activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);

        [PreserveSig]
        int OpenPropertyStore(StorageAccessMode accessMode, out IPropertyStore properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out DeviceState state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute(int muted, ref Guid eventContext);
        [PreserveSig] int GetMute(out int muted);
    }
}
