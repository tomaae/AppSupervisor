using System.Security.Cryptography;
using System.Text.Json;

namespace AppSupervisor.Twitch;

/// <summary>
/// Persists Twitch OAuth credentials in an atomic, per-user DPAPI-protected file and migrates
/// the legacy Windows Credential Manager entry once.
/// </summary>
internal sealed class ProtectedFileTwitchCredentialStore : ITwitchCredentialStore
{
    private const int CurrentFormatVersion = 1;
    private const string ApplicationDirectoryName = "AppSupervisor";
    private const string AuthorizationFileName = "twitch-authorization.dat";
    private static readonly byte[] AdditionalEntropy =
        "AppSupervisor:Twitch:Authorization:v1"u8.ToArray();

    private readonly string _path;
    private readonly ITwitchCredentialStore _legacyStore;
    private readonly Func<byte[], byte[]> _protect;
    private readonly Func<byte[], byte[]> _unprotect;

    public ProtectedFileTwitchCredentialStore()
        : this(
            GetDefaultPath(),
            new WindowsTwitchCredentialStore(),
            ProtectForCurrentUser,
            UnprotectForCurrentUser)
    {
    }

    internal ProtectedFileTwitchCredentialStore(
        string path,
        ITwitchCredentialStore legacyStore)
        : this(path, legacyStore, ProtectForCurrentUser, UnprotectForCurrentUser)
    {
    }

    internal ProtectedFileTwitchCredentialStore(
        string path,
        ITwitchCredentialStore legacyStore,
        Func<byte[], byte[]> protect,
        Func<byte[], byte[]> unprotect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _legacyStore = legacyStore ?? throw new ArgumentNullException(nameof(legacyStore));
        _protect = protect ?? throw new ArgumentNullException(nameof(protect));
        _unprotect = unprotect ?? throw new ArgumentNullException(nameof(unprotect));
    }

    public TwitchStoredAuthorization? Load()
    {
        if (File.Exists(_path))
            return ReadEnvelope().Authorization;

        TwitchStoredAuthorization? legacyAuthorization = _legacyStore.Load();
        WriteEnvelope(legacyAuthorization);
        TryDeleteLegacyAuthorization();
        return legacyAuthorization;
    }

    public void Save(TwitchStoredAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        bool legacyMayBeAuthoritative = !File.Exists(_path);
        WriteEnvelope(authorization);
        if (legacyMayBeAuthoritative)
            TryDeleteLegacyAuthorization();
    }

    public void Delete()
    {
        // A protected tombstone distinguishes an intentional disconnect from a machine that
        // has not migrated yet, so an undeletable stale legacy entry cannot be resurrected.
        bool legacyMayBeAuthoritative = !File.Exists(_path);
        WriteEnvelope(null);
        if (legacyMayBeAuthoritative)
            TryDeleteLegacyAuthorization();
    }

    private AuthorizationEnvelope ReadEnvelope()
    {
        byte[] protectedBytes = File.ReadAllBytes(_path);
        byte[] plainBytes = _unprotect(protectedBytes);
        try
        {
            AuthorizationEnvelope envelope = JsonSerializer.Deserialize<AuthorizationEnvelope>(plainBytes)
                ?? throw new InvalidDataException("The protected Twitch authorization file is empty.");
            if (envelope.Version != CurrentFormatVersion)
            {
                throw new InvalidDataException(
                    $"The protected Twitch authorization uses unsupported format version {envelope.Version}."
                );
            }

            return envelope;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    private void WriteEnvelope(TwitchStoredAuthorization? authorization)
    {
        string directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("The Twitch authorization directory could not be determined.");
        Directory.CreateDirectory(directory);

        byte[] plainBytes = JsonSerializer.SerializeToUtf8Bytes(new AuthorizationEnvelope
        {
            Version = CurrentFormatVersion,
            Authorization = authorization
        });
        byte[] protectedBytes;
        try
        {
            protectedBytes = _protect(plainBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp"
        );

        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(protectedBytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
                File.Replace(temporaryPath, _path, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, _path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private void TryDeleteLegacyAuthorization()
    {
        try
        {
            _legacyStore.Delete();
        }
        catch (Exception exception)
        {
            // The protected file is authoritative after it is written. A legacy cleanup
            // failure must not make a successfully persisted token appear to have failed.
            SupervisorLog.WriteError(
                "The legacy Twitch authorization could not be removed from Windows Credential Manager.",
                exception
            );
        }
    }

    private static string GetDefaultPath()
    {
        string localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData
        );
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException(
                "The current user's Local Application Data directory could not be determined."
            );
        }

        return Path.Combine(
            localApplicationData,
            ApplicationDirectoryName,
            AuthorizationFileName
        );
    }

    private static byte[] ProtectForCurrentUser(byte[] plainBytes) =>
        ProtectedData.Protect(
            plainBytes,
            AdditionalEntropy,
            DataProtectionScope.CurrentUser
        );

    private static byte[] UnprotectForCurrentUser(byte[] protectedBytes) =>
        ProtectedData.Unprotect(
            protectedBytes,
            AdditionalEntropy,
            DataProtectionScope.CurrentUser
        );

    private sealed class AuthorizationEnvelope
    {
        public int Version { get; set; }
        public TwitchStoredAuthorization? Authorization { get; set; }
    }
}
