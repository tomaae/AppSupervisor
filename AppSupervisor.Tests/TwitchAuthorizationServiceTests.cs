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

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class MemoryCredentialStore : ITwitchCredentialStore
    {
        public TwitchStoredAuthorization? Authorization { get; set; }
        public int SaveCount { get; private set; }
        public TwitchStoredAuthorization? Load() => Authorization;
        public void Save(TwitchStoredAuthorization authorization)
        {
            Authorization = authorization;
            SaveCount++;
        }
        public void Delete() => Authorization = null;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
