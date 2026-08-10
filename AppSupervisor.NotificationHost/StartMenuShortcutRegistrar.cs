using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AppSupervisor.NotificationHost;

/// <summary>
/// Creates the Start Menu shortcut and AppUserModelID required by inbox desktop notifications.
/// </summary>
internal static class StartMenuShortcutRegistrar
{
    private const string AppUserModelId = "AppSupervisor.Desktop";
    private const ushort VariantString = 31;
    private const uint ReadWritePropertyStore = 0x00000002;
    private static readonly Guid PropertyStoreInterfaceId = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly PropertyKey AppUserModelIdKey = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        5
    );

    /// <summary>
    /// Creates or refreshes the current user's AppSupervisor Start Menu shortcut.
    /// </summary>
    /// <param name="executablePath">The main executable launched when the shortcut is selected.</param>
    /// <returns><see langword="true"/> when the shortcut and AppUserModelID are saved successfully.</returns>
    public static bool TryRegister(string executablePath)
    {
        object? shell = null;
        object? shortcut = null;
        IPropertyStore? propertyStore = null;

        try
        {
            string programsDirectory = Environment.GetFolderPath(
                Environment.SpecialFolder.Programs
            );
            string shortcutPath = Path.Combine(programsDirectory, "AppSupervisor.lnk");
            string workingDirectory = Path.GetDirectoryName(executablePath) ?? "";

            Directory.CreateDirectory(programsDirectory);

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");

            if (shellType is null)
                return false;

            shell = Activator.CreateInstance(shellType);

            if (shell is null)
                return false;

            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [shortcutPath],
                culture: CultureInfo.InvariantCulture
            );

            if (shortcut is null)
                return false;

            Type shortcutType = shortcut.GetType();
            SetShortcutProperty(shortcutType, shortcut, "TargetPath", executablePath);
            SetShortcutProperty(shortcutType, shortcut, "Arguments", "");
            SetShortcutProperty(shortcutType, shortcut, "Description", "AppSupervisor");
            SetShortcutProperty(shortcutType, shortcut, "WorkingDirectory", workingDirectory);
            SetShortcutProperty(shortcutType, shortcut, "IconLocation", $"{executablePath},0");
            shortcutType.InvokeMember(
                "Save",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shortcut,
                args: null,
                culture: CultureInfo.InvariantCulture
            );

            Guid interfaceId = PropertyStoreInterfaceId;
            SHGetPropertyStoreFromParsingName(
                shortcutPath,
                IntPtr.Zero,
                ReadWritePropertyStore,
                ref interfaceId,
                out propertyStore
            );

            PropertyKey key = AppUserModelIdKey;
            var value = new PropVariant(AppUserModelId);

            try
            {
                propertyStore.SetValue(ref key, ref value);
                propertyStore.Commit();
            }
            finally
            {
                value.Dispose();
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (propertyStore is not null && Marshal.IsComObject(propertyStore))
                Marshal.FinalReleaseComObject(propertyStore);

            if (shortcut is not null && Marshal.IsComObject(shortcut))
                Marshal.FinalReleaseComObject(shortcut);

            if (shell is not null && Marshal.IsComObject(shell))
                Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>
    /// Assigns one string property on the WScript shortcut automation object.
    /// </summary>
    /// <param name="shortcutType">The runtime COM shortcut type.</param>
    /// <param name="shortcut">The shortcut automation object.</param>
    /// <param name="propertyName">The property name to assign.</param>
    /// <param name="value">The string value to store.</param>
    private static void SetShortcutProperty(
        Type shortcutType,
        object shortcut,
        string propertyName,
        string value)
    {
        shortcutType.InvokeMember(
            propertyName,
            BindingFlags.SetProperty,
            binder: null,
            target: shortcut,
            args: [value],
            culture: CultureInfo.InvariantCulture
        );
    }

    /// <summary>
    /// Opens a filesystem item's Windows property store.
    /// </summary>
    /// <param name="path">The shortcut path whose properties will be changed.</param>
    /// <param name="bindingContext">An optional shell binding context.</param>
    /// <param name="flags">The requested property-store access flags.</param>
    /// <param name="interfaceId">The requested COM interface identifier.</param>
    /// <param name="propertyStore">Receives the opened property store.</param>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHGetPropertyStoreFromParsingName(
        string path,
        IntPtr bindingContext,
        uint flags,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    /// <summary>
    /// Defines the property-store operations used to assign the shortcut AppUserModelID.
    /// </summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        /// <summary>
        /// Reads the number of stored properties.
        /// </summary>
        void GetCount(out uint propertyCount);

        /// <summary>
        /// Reads a property key by index.
        /// </summary>
        void GetAt(uint propertyIndex, out PropertyKey key);

        /// <summary>
        /// Reads a property value.
        /// </summary>
        void GetValue([In] ref PropertyKey key, out PropVariant value);

        /// <summary>
        /// Sets a property value.
        /// </summary>
        void SetValue([In] ref PropertyKey key, [In] ref PropVariant value);

        /// <summary>
        /// Saves pending property changes.
        /// </summary>
        void Commit();
    }

    /// <summary>
    /// Identifies one property in a Windows property store.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct PropertyKey
    {
        private readonly Guid _formatId;
        private readonly uint _propertyId;

        /// <summary>
        /// Creates a Windows property key.
        /// </summary>
        /// <param name="formatId">The property-set identifier.</param>
        /// <param name="propertyId">The property identifier inside the set.</param>
        public PropertyKey(Guid formatId, uint propertyId)
        {
            _formatId = formatId;
            _propertyId = propertyId;
        }
    }

    /// <summary>
    /// Owns the unmanaged string memory used for a COM property value.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant : IDisposable
    {
        [FieldOffset(0)]
        private readonly ushort _variantType;

        [FieldOffset(8)]
        private IntPtr _pointerValue;

        /// <summary>
        /// Creates a VT_LPWSTR property value from managed text.
        /// </summary>
        /// <param name="value">The string stored in the property value.</param>
        public PropVariant(string value)
        {
            _variantType = VariantString;
            _pointerValue = Marshal.StringToCoTaskMemUni(value);
        }

        /// <summary>
        /// Releases the unmanaged string allocated for the property value.
        /// </summary>
        public void Dispose()
        {
            if (_pointerValue == IntPtr.Zero)
                return;

            Marshal.FreeCoTaskMem(_pointerValue);
            _pointerValue = IntPtr.Zero;
        }
    }
}
