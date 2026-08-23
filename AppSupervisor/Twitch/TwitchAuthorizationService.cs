using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AppSupervisor.Twitch;

/// <summary>Owns the persistent public-client device authorization and token refresh lifecycle.</summary>
internal sealed class TwitchAuthorizationService : IDisposable
{
    internal const string RequiredScopes =
        "moderator:manage:chat_settings user:write:chat channel:edit:commercial";
    private static readonly Uri IdentityBase = new("https://id.twitch.tv/");
    private static readonly SemaphoreSlim CredentialGate = new(1, 1);
    private static readonly Mutex CrossProcessCredentialGate = new(
        initiallyOwned: false,
        name: @"Local\AppSupervisor.TwitchCredentialRefresh"
    );
    private readonly TwitchIntegrationConfig _configuration;
    private readonly ITwitchCredentialStore _store;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private DateTimeOffset _lastValidatedUtc = DateTimeOffset.MinValue;

    public TwitchAuthorizationService(TwitchIntegrationConfig configuration)
        : this(configuration, new WindowsTwitchCredentialStore(), new HttpClient(), true)
    {
    }

    internal TwitchAuthorizationService(
        TwitchIntegrationConfig configuration,
        ITwitchCredentialStore store,
        HttpClient httpClient,
        bool ownsHttpClient = false)
    {
        _configuration = configuration;
        _store = store;
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<TwitchDeviceAuthorization> BeginConnectAsync(CancellationToken cancellationToken)
    {
        string clientId = RequireClientId();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(IdentityBase, "oauth2/device"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scopes"] = RequiredScopes
            })
        };
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = await ReadDocumentAsync(response, "start Twitch authorization", cancellationToken)
            .ConfigureAwait(false);
        JsonElement root = document.RootElement;
        return new TwitchDeviceAuthorization(
            RequiredString(root, "device_code"),
            RequiredString(root, "user_code"),
            new Uri(RequiredString(root, "verification_uri")),
            DateTimeOffset.UtcNow.AddSeconds(RequiredInt(root, "expires_in")),
            TimeSpan.FromSeconds(Math.Max(1, RequiredInt(root, "interval")))
        );
    }

    public async Task<TwitchAuthorizationStatus> CompleteConnectAsync(
        TwitchDeviceAuthorization authorization,
        CancellationToken cancellationToken)
    {
        string clientId = RequireClientId();

        while (DateTimeOffset.UtcNow < authorization.ExpiresAtUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(IdentityBase, "oauth2/token"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["scopes"] = RequiredScopes,
                    ["device_code"] = authorization.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                })
            };
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.BadRequest &&
                    body.Contains("authorization_pending", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(authorization.PollInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw CreateApiException(response, body, "complete Twitch authorization");
            }

            using JsonDocument document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
            );
            TwitchStoredAuthorization stored = CreateStoredAuthorization(clientId, document.RootElement);
            TwitchAccess access = await ValidateAsync(stored, cancellationToken).ConfigureAwait(false);
            stored.UserId = access.UserId;
            stored.Login = access.Login;
            _store.Save(stored);
            _lastValidatedUtc = DateTimeOffset.UtcNow;
            return new TwitchAuthorizationStatus(true, access.Login);
        }

        throw new InvalidOperationException("The Twitch authorization code expired. Start the connection again.");
    }

    public async Task<TwitchAuthorizationStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            TwitchAccess access = await GetAccessAsync(cancellationToken).ConfigureAwait(false);
            return new TwitchAuthorizationStatus(true, access.Login);
        }
        catch (TwitchNotConnectedException)
        {
            return TwitchAuthorizationStatus.Disconnected;
        }
    }

    public async Task<TwitchAccess> GetAccessAsync(CancellationToken cancellationToken)
    {
        await CredentialGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TwitchStoredAuthorization stored = LoadMatchingAuthorization();
            if (stored.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(1))
                stored = await RefreshNoLockAsync(stored, cancellationToken).ConfigureAwait(false);

            if (DateTimeOffset.UtcNow - _lastValidatedUtc >= TimeSpan.FromHours(1))
            {
                try
                {
                    TwitchAccess validated = await ValidateAsync(stored, cancellationToken).ConfigureAwait(false);
                    stored.UserId = validated.UserId;
                    stored.Login = validated.Login;
                    _store.Save(stored);
                    _lastValidatedUtc = DateTimeOffset.UtcNow;
                    return validated;
                }
                catch (TwitchUnauthorizedException)
                {
                    stored = await RefreshNoLockAsync(stored, cancellationToken).ConfigureAwait(false);
                }
            }

            return new TwitchAccess(stored.ClientId, stored.AccessToken, stored.UserId, stored.Login);
        }
        finally
        {
            CredentialGate.Release();
        }
    }

    public async Task<TwitchAccess> ForceRefreshAsync(
        string rejectedAccessToken,
        CancellationToken cancellationToken)
    {
        await CredentialGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TwitchStoredAuthorization stored = LoadMatchingAuthorization();

            // A different request may have refreshed the one-time-use credential while this
            // request was waiting for the shared gate. Reuse that persisted replacement.
            if (string.Equals(
                stored.AccessToken,
                rejectedAccessToken,
                StringComparison.Ordinal))
            {
                stored = await RefreshNoLockAsync(stored, cancellationToken).ConfigureAwait(false);
            }

            return new TwitchAccess(stored.ClientId, stored.AccessToken, stored.UserId, stored.Login);
        }
        finally
        {
            CredentialGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        TwitchStoredAuthorization? stored = _store.Load();
        try
        {
            if (stored is not null && !string.IsNullOrWhiteSpace(stored.AccessToken))
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(IdentityBase, "oauth2/revoke"))
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["client_id"] = stored.ClientId,
                        ["token"] = stored.AccessToken
                    })
                };
                using HttpResponseMessage _ = await _httpClient.SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _store.Delete();
            _lastValidatedUtc = DateTimeOffset.MinValue;
        }
    }

    private TwitchStoredAuthorization LoadMatchingAuthorization()
    {
        string clientId = RequireClientId();
        TwitchStoredAuthorization? stored = _store.Load();
        if (stored is null)
            throw new TwitchNotConnectedException();
        if (!string.Equals(stored.ClientId, clientId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The stored Twitch authorization belongs to a different AppSupervisor application identity. Reconnect Twitch."
            );
        if (string.IsNullOrWhiteSpace(stored.RefreshToken))
            throw new InvalidOperationException("The stored Twitch authorization cannot be refreshed. Reconnect Twitch.");
        return stored;
    }

    private async Task<TwitchStoredAuthorization> RefreshNoLockAsync(
        TwitchStoredAuthorization stored,
        CancellationToken cancellationToken)
    {
        return await Task.Run(
            () => RefreshAcrossProcesses(stored, cancellationToken),
            CancellationToken.None
        ).ConfigureAwait(false);
    }

    private TwitchStoredAuthorization RefreshAcrossProcesses(
        TwitchStoredAuthorization expected,
        CancellationToken cancellationToken)
    {
        bool acquired = false;
        try
        {
            try
            {
                int signaled = WaitHandle.WaitAny(
                    [CrossProcessCredentialGate, cancellationToken.WaitHandle]
                );
                if (signaled != 0)
                    throw new OperationCanceledException(cancellationToken);
                acquired = true;
            }
            catch (AbandonedMutexException exception) when (exception.MutexIndex == 0)
            {
                acquired = true;
            }

            TwitchStoredAuthorization current = LoadMatchingAuthorization();
            if (!IsSameCredentialVersion(current, expected))
                return current;

            return RefreshOwnedAsync(current, cancellationToken).GetAwaiter().GetResult();
        }
        finally
        {
            if (acquired)
                CrossProcessCredentialGate.ReleaseMutex();
        }
    }

    private async Task<TwitchStoredAuthorization> RefreshOwnedAsync(
        TwitchStoredAuthorization stored,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(IdentityBase, "oauth2/token"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = stored.ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = stored.RefreshToken
            })
        };
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if ((response.StatusCode == HttpStatusCode.BadRequest ||
                    response.StatusCode == HttpStatusCode.Unauthorized) &&
                body.Contains("invalid refresh token", StringComparison.OrdinalIgnoreCase))
            {
                // A concurrently running older AppSupervisor build may not honor the
                // cross-process gate. Prefer its persisted rotation if one appeared while
                // this request was in flight instead of treating the session as lost.
                TwitchStoredAuthorization replacement = LoadMatchingAuthorization();
                if (!IsSameCredentialVersion(replacement, stored))
                    return replacement;

                throw new InvalidOperationException(
                    "The stored Twitch authorization can no longer be refreshed. Reconnect Twitch once."
                );
            }

            throw CreateApiException(response, body, "refresh Twitch authorization");
        }

        using JsonDocument document = JsonDocument.Parse(body);
        TwitchStoredAuthorization refreshed = CreateStoredAuthorization(stored.ClientId, document.RootElement);
        refreshed.UserId = stored.UserId;
        refreshed.Login = stored.Login;
        _store.Save(refreshed);
        TwitchAccess validated = await ValidateAsync(refreshed, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(refreshed.UserId, validated.UserId, StringComparison.Ordinal) ||
            !string.Equals(refreshed.Login, validated.Login, StringComparison.Ordinal))
        {
            refreshed.UserId = validated.UserId;
            refreshed.Login = validated.Login;
            _store.Save(refreshed);
        }
        _lastValidatedUtc = DateTimeOffset.UtcNow;
        return refreshed;
    }

    private static bool IsSameCredentialVersion(
        TwitchStoredAuthorization first,
        TwitchStoredAuthorization second) =>
        string.Equals(first.AccessToken, second.AccessToken, StringComparison.Ordinal) &&
        string.Equals(first.RefreshToken, second.RefreshToken, StringComparison.Ordinal);

    private async Task<TwitchAccess> ValidateAsync(
        TwitchStoredAuthorization stored,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(IdentityBase, "oauth2/validate"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stored.AccessToken);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new TwitchUnauthorizedException();
        if (!response.IsSuccessStatusCode)
            throw CreateApiException(response, body, "validate Twitch authorization");

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        string clientId = RequiredString(root, "client_id");
        if (!string.Equals(clientId, stored.ClientId, StringComparison.Ordinal))
            throw new InvalidOperationException("The Twitch authorization belongs to a different Client ID.");
        var grantedScopes = root.TryGetProperty("scopes", out JsonElement scopes) &&
            scopes.ValueKind == JsonValueKind.Array
            ? scopes.EnumerateArray()
                .Select(scope => scope.GetString() ?? "")
                .ToHashSet(StringComparer.Ordinal)
            : [];
        string[] missingScopes = RequiredScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(scope => !grantedScopes.Contains(scope))
            .ToArray();
        if (missingScopes.Length > 0)
        {
            throw new InvalidOperationException(
                "The Twitch authorization is missing required permissions. Reconnect Twitch. Missing: " +
                string.Join(", ", missingScopes)
            );
        }
        return new TwitchAccess(
            clientId,
            stored.AccessToken,
            RequiredString(root, "user_id"),
            RequiredString(root, "login")
        );
    }

    private static TwitchStoredAuthorization CreateStoredAuthorization(string clientId, JsonElement root) => new()
    {
        ClientId = clientId,
        AccessToken = RequiredString(root, "access_token"),
        RefreshToken = RequiredString(root, "refresh_token"),
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(RequiredInt(root, "expires_in"))
    };

    private string RequireClientId()
    {
        return TwitchApplication.ClientId;
    }

    private static async Task<JsonDocument> ReadDocumentAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw CreateApiException(response, body, operation);
        return JsonDocument.Parse(body);
    }

    private static Exception CreateApiException(HttpResponseMessage response, string body, string operation)
    {
        string detail = "";
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out JsonElement message))
                detail = message.GetString() ?? "";
        }
        catch (JsonException)
        {
        }
        return new InvalidOperationException(
            $"Twitch could not {operation} ({(int)response.StatusCode} {response.ReasonPhrase})." +
            (detail.Length == 0 ? "" : $" {detail}")
        );
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidOperationException($"Twitch returned an incomplete response ({name} is missing).");

    private static int RequiredInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result)
            ? result
            : throw new InvalidOperationException($"Twitch returned an incomplete response ({name} is missing).");

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private sealed class TwitchNotConnectedException : InvalidOperationException;
    private sealed class TwitchUnauthorizedException : Exception;
}
