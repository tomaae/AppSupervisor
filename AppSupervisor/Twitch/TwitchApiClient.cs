using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AppSupervisor.Twitch;

/// <summary>Calls the Twitch Helix endpoints needed by broadcaster profile actions.</summary>
internal sealed class TwitchApiClient : ITwitchApiClient
{
    private static readonly Uri ApiBase = new("https://api.twitch.tv/helix/");
    private readonly TwitchAuthorizationService _authorization;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public TwitchApiClient(TwitchIntegrationConfig integration)
        : this(new TwitchAuthorizationService(integration), new HttpClient(), true)
    {
    }

    internal TwitchApiClient(
        TwitchAuthorizationService authorization,
        HttpClient httpClient,
        bool ownsHttpClient = false)
    {
        _authorization = authorization;
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<TwitchChatSettings> GetChatSettingsAsync(CancellationToken cancellationToken)
    {
        using JsonDocument document = await SendAsync(
            access => CreateRequest(
                HttpMethod.Get,
                $"chat/settings?broadcaster_id={Uri.EscapeDataString(access.UserId)}&moderator_id={Uri.EscapeDataString(access.UserId)}",
                access
            ),
            "read chat settings",
            cancellationToken
        ).ConfigureAwait(false);
        JsonElement item = FirstData(document.RootElement, "chat settings");
        return new TwitchChatSettings(
            item.GetProperty("emote_mode").GetBoolean(),
            item.GetProperty("follower_mode").GetBoolean(),
            OptionalInt(item, "follower_mode_duration"),
            item.GetProperty("slow_mode").GetBoolean(),
            OptionalInt(item, "slow_mode_wait_time"),
            item.GetProperty("subscriber_mode").GetBoolean()
        );
    }

    public async Task UpdateChatSettingsAsync(
        TwitchChatSettingsUpdate update,
        CancellationToken cancellationToken)
    {
        using JsonDocument _ = await SendAsync(
            access =>
            {
                HttpRequestMessage request = CreateRequest(
                    HttpMethod.Patch,
                    $"chat/settings?broadcaster_id={Uri.EscapeDataString(access.UserId)}&moderator_id={Uri.EscapeDataString(access.UserId)}",
                    access
                );
                var body = new Dictionary<string, object>();
                Add(body, "emote_mode", update.EmoteMode);
                Add(body, "follower_mode", update.FollowerMode);
                Add(body, "follower_mode_duration", update.FollowerModeDuration);
                Add(body, "slow_mode", update.SlowMode);
                Add(body, "slow_mode_wait_time", update.SlowModeWaitTime);
                Add(body, "subscriber_mode", update.SubscriberMode);
                request.Content = JsonContent.Create(body);
                return request;
            },
            "update chat settings",
            cancellationToken
        ).ConfigureAwait(false);
    }

    public async Task SendChatMessageAsync(string message, CancellationToken cancellationToken)
    {
        using JsonDocument document = await SendAsync(
            access =>
            {
                HttpRequestMessage request = CreateRequest(HttpMethod.Post, "chat/messages", access);
                request.Content = JsonContent.Create(new
                {
                    broadcaster_id = access.UserId,
                    sender_id = access.UserId,
                    message
                });
                return request;
            },
            "send the chat message",
            cancellationToken
        ).ConfigureAwait(false);
        JsonElement item = FirstData(document.RootElement, "chat message result");
        if (!item.GetProperty("is_sent").GetBoolean())
        {
            string reason = item.TryGetProperty("drop_reason", out JsonElement drop) &&
                drop.ValueKind == JsonValueKind.Object && drop.TryGetProperty("message", out JsonElement detail)
                ? detail.GetString() ?? "Twitch dropped the message."
                : "Twitch dropped the message.";
            throw new InvalidOperationException(reason);
        }
    }

    public async Task RunCommercialAsync(int lengthSeconds, CancellationToken cancellationToken)
    {
        using JsonDocument _ = await SendAsync(
            access =>
            {
                HttpRequestMessage request = CreateRequest(HttpMethod.Post, "channels/commercial", access);
                request.Content = JsonContent.Create(new
                {
                    broadcaster_id = access.UserId,
                    length = lengthSeconds
                });
                return request;
            },
            "start the commercial",
            cancellationToken
        ).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendAsync(
        Func<TwitchAccess, HttpRequestMessage> requestFactory,
        string operation,
        CancellationToken cancellationToken)
    {
        TwitchAccess access = await _authorization.GetAccessAsync(cancellationToken).ConfigureAwait(false);
        HttpResponseMessage response = await SendOnceAsync(requestFactory(access), cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            access = await _authorization.ForceRefreshAsync(cancellationToken).ConfigureAwait(false);
            response = await SendOnceAsync(requestFactory(access), cancellationToken).ConfigureAwait(false);
        }

        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw CreateApiException(response, body, operation);
            return string.IsNullOrWhiteSpace(body)
                ? JsonDocument.Parse("{\"data\":[]}")
                : JsonDocument.Parse(body);
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using (request)
            return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string relativeUri, TwitchAccess access)
    {
        var request = new HttpRequestMessage(method, new Uri(ApiBase, relativeUri));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.AccessToken);
        request.Headers.Add("Client-Id", access.ClientId);
        return request;
    }

    private static JsonElement FirstData(JsonElement root, string description)
    {
        if (root.TryGetProperty("data", out JsonElement data) &&
            data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
        {
            return data[0];
        }
        throw new InvalidOperationException($"Twitch returned no {description}.");
    }

    private static int? OptionalInt(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static void Add(Dictionary<string, object> body, string key, bool? value)
    {
        if (value.HasValue)
            body[key] = value.Value;
    }

    private static void Add(Dictionary<string, object> body, string key, int? value)
    {
        if (value.HasValue)
            body[key] = value.Value;
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

    public void Dispose()
    {
        _authorization.Dispose();
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
