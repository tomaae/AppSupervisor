using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AppSupervisor.Twitch;

/// <summary>Persists Twitch OAuth credentials in Windows Credential Manager for the current user.</summary>
internal sealed class WindowsTwitchCredentialStore : ITwitchCredentialStore
{
    private const string TargetName = "AppSupervisor:Twitch";
    private const int CredentialTypeGeneric = 1;
    private const int CredentialPersistLocalMachine = 2;

    public TwitchStoredAuthorization? Load()
    {
        if (!CredRead(TargetName, CredentialTypeGeneric, 0, out nint pointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 1168)
                return null;
            throw CreateCredentialManagerException(error, "read the stored Twitch authorization");
        }

        try
        {
            CREDENTIAL credential = Marshal.PtrToStructure<CREDENTIAL>(pointer);
            if (credential.CredentialBlob == nint.Zero || credential.CredentialBlobSize == 0)
                return null;

            byte[] bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return JsonSerializer.Deserialize<TwitchStoredAuthorization>(bytes);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Save(TwitchStoredAuthorization authorization)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(authorization);
        nint blob = Marshal.AllocCoTaskMem(bytes.Length);

        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new CREDENTIAL
            {
                Type = CredentialTypeGeneric,
                TargetName = TargetName,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = authorization.Login
            };

            if (!CredWrite(ref credential, 0))
            {
                throw CreateCredentialManagerException(
                    Marshal.GetLastWin32Error(),
                    "store the Twitch authorization"
                );
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public void Delete()
    {
        if (!CredDelete(TargetName, CredentialTypeGeneric, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 1168)
                throw CreateCredentialManagerException(error, "remove the Twitch authorization");
        }
    }

    internal static Win32Exception CreateCredentialManagerException(int error, string operation)
    {
        string systemMessage = new Win32Exception(error).Message;
        return new Win32Exception(
            error,
            $"Windows Credential Manager could not {operation}. Credential Manager error {error}: {systemMessage}"
        );
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int flags, out nint credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint credential);
}
