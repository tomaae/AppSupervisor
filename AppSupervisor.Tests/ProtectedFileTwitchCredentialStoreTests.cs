using System.ComponentModel;
using System.Text;
using AppSupervisor.Twitch;

namespace AppSupervisor.Tests;

/// <summary>Protects durable Twitch authorization storage and one-time legacy migration.</summary>
public sealed class ProtectedFileTwitchCredentialStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsWithoutWritingPlaintextTokens()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "twitch-authorization.dat");
        try
        {
            var legacy = new FakeCredentialStore();
            var store = new ProtectedFileTwitchCredentialStore(path, legacy);
            TwitchStoredAuthorization expected = CreateAuthorization("secret-access", "secret-refresh");

            store.Save(expected);
            TwitchStoredAuthorization? actual =
                new ProtectedFileTwitchCredentialStore(path, legacy).Load();

            Assert.NotNull(actual);
            Assert.Equal(expected.ClientId, actual.ClientId);
            Assert.Equal(expected.AccessToken, actual.AccessToken);
            Assert.Equal(expected.RefreshToken, actual.RefreshToken);
            string persistedText = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            Assert.DoesNotContain(expected.AccessToken, persistedText, StringComparison.Ordinal);
            Assert.DoesNotContain(expected.RefreshToken, persistedText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_MigratesLegacyAuthorizationOnce()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "twitch-authorization.dat");
        try
        {
            var legacy = new FakeCredentialStore
            {
                Authorization = CreateAuthorization("legacy-access", "legacy-refresh")
            };
            var store = CreateStore(path, legacy);

            TwitchStoredAuthorization? migrated = store.Load();
            TwitchStoredAuthorization? persisted = CreateStore(path, legacy).Load();

            Assert.Equal("legacy-access", migrated!.AccessToken);
            Assert.Equal("legacy-refresh", persisted!.RefreshToken);
            Assert.Equal(1, legacy.LoadCount);
            Assert.Equal(1, legacy.DeleteCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Delete_WritesTombstoneThatCannotResurrectLegacyAuthorization()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "twitch-authorization.dat");
        try
        {
            var legacy = new FakeCredentialStore
            {
                Authorization = CreateAuthorization("stale-access", "stale-refresh"),
                RetainAuthorizationOnDelete = true
            };
            var store = CreateStore(path, legacy);

            store.Delete();
            TwitchStoredAuthorization? loaded = CreateStore(path, legacy).Load();

            Assert.Null(loaded);
            Assert.Equal(0, legacy.LoadCount);
            Assert.Equal(1, legacy.DeleteCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CredentialManagerError_IncludesNativeCodeAndSystemExplanation()
    {
        Win32Exception exception =
            WindowsTwitchCredentialStore.CreateCredentialManagerException(
                1312,
                "store the Twitch authorization"
            );

        Assert.Contains("Credential Manager error 1312", exception.Message, StringComparison.Ordinal);
        Assert.Contains(new Win32Exception(1312).Message, exception.Message, StringComparison.Ordinal);
    }

    private static ProtectedFileTwitchCredentialStore CreateStore(
        string path,
        ITwitchCredentialStore legacyStore) => new(
            path,
            legacyStore,
            bytes => [0xA5, .. bytes.Reverse()],
            bytes => bytes.Skip(1).Reverse().ToArray()
        );

    private static TwitchStoredAuthorization CreateAuthorization(
        string accessToken,
        string refreshToken) => new()
    {
        ClientId = TwitchApplication.ClientId,
        AccessToken = accessToken,
        RefreshToken = refreshToken,
        UserId = "123",
        Login = "broadcaster",
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(4)
    };

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.TwitchCredentialTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeCredentialStore : ITwitchCredentialStore
    {
        public TwitchStoredAuthorization? Authorization { get; set; }
        public bool RetainAuthorizationOnDelete { get; set; }
        public int LoadCount { get; private set; }
        public int DeleteCount { get; private set; }

        public TwitchStoredAuthorization? Load()
        {
            LoadCount++;
            return Authorization;
        }

        public void Save(TwitchStoredAuthorization authorization) =>
            Authorization = authorization;

        public void Delete()
        {
            DeleteCount++;
            if (!RetainAuthorizationOnDelete)
                Authorization = null;
        }
    }
}
