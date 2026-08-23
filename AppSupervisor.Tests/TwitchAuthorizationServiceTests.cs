using System.Net;
using System.Text;
using AppSupervisor.Twitch;

namespace AppSupervisor.Tests;

/// <summary>Verifies silent public-client token rotation and durable replacement.</summary>
public sealed class TwitchAuthorizationServiceTests
{
    [Fact]
    public async Task GetAccess_ExpiredToken_RefreshesAndPersistsRotatedCredentials()
    {
        var store = new MemoryCredentialStore
        {
            Authorization = new TwitchStoredAuthorization
            {
                ClientId = TwitchApplication.ClientId,
                AccessToken = "expired-access",
                RefreshToken = "old-refresh",
                UserId = "123",
                Login = "broadcaster",
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
            }
        };
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK,
                    "{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":14400}");
            }

            return Json(HttpStatusCode.OK,
                $"{{\"client_id\":\"{TwitchApplication.ClientId}\",\"login\":\"broadcaster\",\"user_id\":\"123\",\"scopes\":[\"moderator:manage:chat_settings\",\"user:write:chat\",\"channel:edit:commercial\"],\"expires_in\":14000}}");
        }));
        using var service = new TwitchAuthorizationService(
            new TwitchIntegrationConfig(),
            store,
            httpClient
        );

        TwitchAccess access = await service.GetAccessAsync(CancellationToken.None);

        Assert.Equal("new-access", access.AccessToken);
        Assert.Equal("new-refresh", store.Authorization!.RefreshToken);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task GetAccess_ValidationFailsAfterRefresh_PersistsRotatedCredentialsFirst()
    {
        var store = CreateExpiredStore();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK,
                    "{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":14400}");
            }

            return Json(HttpStatusCode.ServiceUnavailable, "{\"message\":\"temporarily unavailable\"}");
        }));
        using var service = new TwitchAuthorizationService(
            new TwitchIntegrationConfig(),
            store,
            httpClient
        );

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetAccessAsync(CancellationToken.None)
        );

        Assert.Equal("new-access", store.Authorization!.AccessToken);
        Assert.Equal("new-refresh", store.Authorization.RefreshToken);
        Assert.Equal("123", store.Authorization.UserId);
        Assert.Equal("broadcaster", store.Authorization.Login);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task GetStatus_InvalidRefreshToken_SurfacesReconnectRequirement()
    {
        var store = CreateExpiredStore();
        using var httpClient = new HttpClient(new StubHandler(_ =>
            Json(HttpStatusCode.BadRequest, "{\"message\":\"Invalid refresh token\"}")));
        using var service = new TwitchAuthorizationService(
            new TwitchIntegrationConfig(),
            store,
            httpClient
        );

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetStatusAsync(CancellationToken.None)
        );

        Assert.Contains("Reconnect Twitch once", exception.Message);
    }

    [Fact]
    public async Task GetAccess_RefreshRejectedAfterExternalRotation_ReusesPersistedReplacement()
    {
        var store = CreateExpiredStore();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
            {
                store.Replace(new TwitchStoredAuthorization
                {
                    ClientId = TwitchApplication.ClientId,
                    AccessToken = "externally-rotated-access",
                    RefreshToken = "externally-rotated-refresh",
                    UserId = "123",
                    Login = "broadcaster",
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(4)
                });
                return Json(HttpStatusCode.BadRequest, "{\"message\":\"Invalid refresh token\"}");
            }

            return Json(HttpStatusCode.OK,
                $"{{\"client_id\":\"{TwitchApplication.ClientId}\",\"login\":\"broadcaster\",\"user_id\":\"123\",\"scopes\":[\"moderator:manage:chat_settings\",\"user:write:chat\",\"channel:edit:commercial\"],\"expires_in\":14000}}");
        }));
        using var service = new TwitchAuthorizationService(
            new TwitchIntegrationConfig(),
            store,
            httpClient
        );

        TwitchAccess access = await service.GetAccessAsync(CancellationToken.None);

        Assert.Equal("externally-rotated-access", access.AccessToken);
        Assert.Equal("externally-rotated-refresh", store.Authorization!.RefreshToken);
    }

    [Fact]
    public async Task GetStatus_MissingCredential_ReturnsDisconnected()
    {
        var store = new MemoryCredentialStore();
        using var httpClient = new HttpClient(new StubHandler(_ =>
            throw new InvalidOperationException("No HTTP request was expected.")));
        using var service = new TwitchAuthorizationService(
            new TwitchIntegrationConfig(),
            store,
            httpClient
        );

        TwitchAuthorizationStatus status = await service.GetStatusAsync(CancellationToken.None);

        Assert.False(status.Connected);
    }

    [Fact]
    public async Task GetAccess_ConcurrentServices_RefreshSharedCredentialOnce()
    {
        var store = CreateExpiredStore();
        int refreshCount = 0;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref refreshCount);
                return Json(HttpStatusCode.OK,
                    "{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":14400}");
            }

            return Json(HttpStatusCode.OK,
                $"{{\"client_id\":\"{TwitchApplication.ClientId}\",\"login\":\"broadcaster\",\"user_id\":\"123\",\"scopes\":[\"moderator:manage:chat_settings\",\"user:write:chat\",\"channel:edit:commercial\"],\"expires_in\":14000}}");
        }));
        using var first = new TwitchAuthorizationService(
            new TwitchIntegrationConfig(),
            store,
            httpClient
        );
        using var second = new TwitchAuthorizationService(
            new TwitchIntegrationConfig(),
            store,
            httpClient
        );

        TwitchAccess[] access = await Task.WhenAll(
            first.GetAccessAsync(CancellationToken.None),
            second.GetAccessAsync(CancellationToken.None)
        );

        Assert.Equal(1, refreshCount);
        Assert.All(access, item => Assert.Equal("new-access", item.AccessToken));
        Assert.Equal("new-refresh", store.Authorization!.RefreshToken);
    }

    [Fact]
    public async Task ForceRefresh_ConcurrentRejectedAccess_ConsumesRefreshTokenOnce()
    {
        var store = CreateValidStore();
        int refreshCount = 0;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref refreshCount);
                return Json(HttpStatusCode.OK,
                    "{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":14400}");
            }

            return Json(HttpStatusCode.OK,
                $"{{\"client_id\":\"{TwitchApplication.ClientId}\",\"login\":\"broadcaster\",\"user_id\":\"123\",\"scopes\":[\"moderator:manage:chat_settings\",\"user:write:chat\",\"channel:edit:commercial\"],\"expires_in\":14000}}");
        }));
        using var first = new TwitchAuthorizationService(
            new TwitchIntegrationConfig(),
            store,
            httpClient
        );
        using var second = new TwitchAuthorizationService(
            new TwitchIntegrationConfig(),
            store,
            httpClient
        );

        TwitchAccess[] access = await Task.WhenAll(
            first.ForceRefreshAsync("old-access", CancellationToken.None),
            second.ForceRefreshAsync("old-access", CancellationToken.None)
        );

        Assert.Equal(1, refreshCount);
        Assert.All(access, item => Assert.Equal("new-access", item.AccessToken));
        Assert.Equal("new-refresh", store.Authorization!.RefreshToken);
    }

    [Fact]
    public async Task ForceRefresh_StoredTokenAlreadyChanged_ReusesPersistedReplacement()
    {
        var store = CreateValidStore();
        using var httpClient = new HttpClient(new StubHandler(_ =>
            throw new InvalidOperationException("No HTTP request was expected.")));
        using var service = new TwitchAuthorizationService(
            new TwitchIntegrationConfig(),
            store,
            httpClient
        );

        TwitchAccess access = await service.ForceRefreshAsync(
            "different-rejected-access",
            CancellationToken.None
        );

        Assert.Equal("old-access", access.AccessToken);
        Assert.Equal(0, store.SaveCount);
    }

    private static MemoryCredentialStore CreateExpiredStore() => new()
    {
        Authorization = new TwitchStoredAuthorization
        {
            ClientId = TwitchApplication.ClientId,
            AccessToken = "expired-access",
            RefreshToken = "old-refresh",
            UserId = "123",
            Login = "broadcaster",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        }
    };

    private static MemoryCredentialStore CreateValidStore() => new()
    {
        Authorization = new TwitchStoredAuthorization
        {
            ClientId = TwitchApplication.ClientId,
            AccessToken = "old-access",
            RefreshToken = "old-refresh",
            UserId = "123",
            Login = "broadcaster",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
        }
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class MemoryCredentialStore : ITwitchCredentialStore
    {
        private readonly object _sync = new();
        public TwitchStoredAuthorization? Authorization { get; set; }
        public int SaveCount { get; private set; }
        public TwitchStoredAuthorization? Load()
        {
            lock (_sync)
                return Authorization;
        }
        public void Save(TwitchStoredAuthorization authorization)
        {
            lock (_sync)
            {
                Authorization = authorization;
                SaveCount++;
            }
        }
        public void Replace(TwitchStoredAuthorization authorization)
        {
            lock (_sync)
                Authorization = authorization;
        }
        public void Delete()
        {
            lock (_sync)
                Authorization = null;
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
