using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AppSupervisor.HomeAssistant;

/// <summary>Calls Home Assistant's authenticated REST API without blocking supervision threads.</summary>
internal sealed class HomeAssistantClient : IHomeAssistantClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
    };
    private readonly HttpClient _httpClient;
    private bool _disposed;

    public HomeAssistantClient(HomeAssistantIntegrationConfig configuration)
        : this(configuration, CreateHttpClient(configuration))
    {
    }

    internal HomeAssistantClient(
        HomeAssistantIntegrationConfig configuration,
        HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _httpClient = httpClient;
    }

    /// <summary>Tests authentication and returns the running Home Assistant version.</summary>
    public static async Task<string> TestConnectionAsync(
        HomeAssistantIntegrationConfig configuration,
        CancellationToken cancellationToken)
    {
        using var client = new HomeAssistantClient(configuration);
        using JsonDocument api = await client.GetJsonAsync("api/", cancellationToken)
            .ConfigureAwait(false);
        string? message = api.RootElement.TryGetProperty("message", out JsonElement messageElement)
            ? messageElement.GetString()
            : null;

        if (!string.Equals(message, "API running.", StringComparison.Ordinal))
            throw new InvalidOperationException("Home Assistant did not return its expected API status response.");

        using JsonDocument config = await client.GetJsonAsync("api/config", cancellationToken)
            .ConfigureAwait(false);
        return config.RootElement.TryGetProperty("version", out JsonElement version)
            ? version.GetString() ?? "unknown"
            : "unknown";
    }

    /// <summary>Discovers supported deterministic services and their selectable entities.</summary>
    public static async Task<HomeAssistantCatalog> LoadCatalogAsync(
        HomeAssistantIntegrationConfig configuration,
        CancellationToken cancellationToken)
    {
        using var client = new HomeAssistantClient(configuration);
        Task<JsonDocument> servicesTask = client.GetJsonAsync("api/services", cancellationToken);
        Task<JsonDocument> statesTask = client.GetJsonAsync("api/states", cancellationToken);
        Task<JsonDocument> configTask = client.GetJsonAsync("api/config", cancellationToken);
        await Task.WhenAll(servicesTask, statesTask, configTask).ConfigureAwait(false);
        using JsonDocument servicesDocument = await servicesTask.ConfigureAwait(false);
        using JsonDocument statesDocument = await statesTask.ConfigureAwait(false);
        using JsonDocument configDocument = await configTask.ConfigureAwait(false);

        IReadOnlyList<HomeAssistantServiceInfo> services = ReadServices(servicesDocument.RootElement);
        IReadOnlyList<HomeAssistantEntityInfo> entities = ReadEntities(statesDocument.RootElement);
        string version = configDocument.RootElement.TryGetProperty("version", out JsonElement versionElement)
            ? versionElement.GetString() ?? "unknown"
            : "unknown";
        return new HomeAssistantCatalog(version, services, entities);
    }

    public async Task CallServiceAsync(
        string service,
        string entityId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string[] parts = service.Split('.', 2);

        if (parts.Length != 2)
            throw new ArgumentException("The Home Assistant service must use domain.service form.", nameof(service));

        using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
            $"api/services/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}",
            new { entity_id = entityId },
            cancellationToken
        ).ConfigureAwait(false);
        EnsureSuccess(response, "call Home Assistant service");
    }

    public async Task<string> GetEntityStateAsync(
        string entityId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using JsonDocument document = await GetJsonAsync(
            $"api/states/{Uri.EscapeDataString(entityId)}",
            cancellationToken
        ).ConfigureAwait(false);
        return document.RootElement.TryGetProperty("state", out JsonElement state)
            ? state.GetString() ?? "unknown"
            : "unknown";
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _httpClient.Dispose();
    }

    private async Task<JsonDocument> GetJsonAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using HttpResponseMessage response = await _httpClient.GetAsync(relativePath, cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response, "query Home Assistant");
        Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static HttpClient CreateHttpClient(HomeAssistantIntegrationConfig configuration)
    {
        string url = configuration.Url?.Trim() ?? "";

        if (!url.EndsWith('/'))
            url += "/";

        var client = new HttpClient(SharedHandler, disposeHandler: false)
        {
            BaseAddress = new Uri(url, UriKind.Absolute),
            Timeout = RequestTimeout
        };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", configuration.Token?.Trim());
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json")
        );
        return client;
    }

    private static IReadOnlyList<HomeAssistantServiceInfo> ReadServices(JsonElement root)
    {
        var result = new List<HomeAssistantServiceInfo>();

        foreach (JsonElement domainElement in root.EnumerateArray())
        {
            if (!domainElement.TryGetProperty("domain", out JsonElement domainProperty) ||
                !domainElement.TryGetProperty("services", out JsonElement servicesProperty))
            {
                continue;
            }

            string domain = domainProperty.GetString() ?? "";

            foreach (JsonProperty serviceProperty in servicesProperty.EnumerateObject())
            {
                string action = serviceProperty.Name;

                if (action is not ("turn_on" or "turn_off") &&
                    !(domain == "button" && action == "press"))
                {
                    continue;
                }

                IReadOnlyList<string> domains = ReadTargetDomains(serviceProperty.Value);

                if (domains.Contains(domain, StringComparer.OrdinalIgnoreCase))
                    result.Add(new HomeAssistantServiceInfo($"{domain}.{action}", domains));
            }
        }

        return result.OrderBy(service => service.Service, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> ReadTargetDomains(JsonElement service)
    {
        if (!service.TryGetProperty("target", out JsonElement target) ||
            !target.TryGetProperty("entity", out JsonElement entityTargets) ||
            entityTargets.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement entityTarget in entityTargets.EnumerateArray())
        {
            if (!entityTarget.TryGetProperty("domain", out JsonElement domainList) ||
                domainList.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement domain in domainList.EnumerateArray())
            {
                string? value = domain.GetString();

                if (!string.IsNullOrWhiteSpace(value))
                    domains.Add(value);
            }
        }

        return domains.OrderBy(domain => domain, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<HomeAssistantEntityInfo> ReadEntities(JsonElement root)
    {
        var entities = new List<HomeAssistantEntityInfo>();

        foreach (JsonElement state in root.EnumerateArray())
        {
            string entityId = state.TryGetProperty("entity_id", out JsonElement idElement)
                ? idElement.GetString() ?? ""
                : "";

            if (string.IsNullOrWhiteSpace(entityId))
                continue;

            string value = state.TryGetProperty("state", out JsonElement stateElement)
                ? stateElement.GetString() ?? "unknown"
                : "unknown";
            string friendlyName = "";

            if (state.TryGetProperty("attributes", out JsonElement attributes) &&
                attributes.TryGetProperty("friendly_name", out JsonElement friendlyNameElement))
            {
                friendlyName = friendlyNameElement.GetString() ?? "";
            }

            entities.Add(new HomeAssistantEntityInfo(entityId, friendlyName, value));
        }

        return entities.OrderBy(entity => entity.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entity => entity.EntityId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode)
            return;

        throw new HttpRequestException(
            $"Could not {operation}: Home Assistant returned " +
            $"{(int)response.StatusCode} {response.ReasonPhrase}."
        );
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
