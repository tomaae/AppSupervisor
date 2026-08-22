using System.Net;
using System.Text.Json;
using AppSupervisor.Core;
using VRC.OSCQuery;

namespace AppSupervisor.Health;

/// <summary>
/// Discovers VRChat's OSCQuery HTTP service and validates its address structure or selected parameter freshness.
/// </summary>
public sealed class VrcOscQueryProbe : IHealthProbe
{
    private const int DiscoverySettleMilliseconds = 350;

    private readonly IReadOnlyList<string> _parameters;
    private readonly TimeSpan? _stalePeriod;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, ParameterState> _parameterStates =
        new(StringComparer.OrdinalIgnoreCase);

    private OSCQueryService? _oscQueryService;
    private bool _disposed;

    /// <summary>Creates a discovery-based VRChat OSCQuery probe with optional parameter freshness tracking.</summary>
    /// <param name="parameters">OSC parameter leaf names to query under /avatar/parameters.</param>
    /// <param name="stalePeriod">The optional unchanged-value duration that constitutes staleness.</param>
    public VrcOscQueryProbe(
        IReadOnlyList<string> parameters,
        TimeSpan? stalePeriod)
    {
        _parameters = parameters;
        _stalePeriod = stalePeriod;
        _httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <summary>Discovers VRChat, queries its OSCQuery endpoint, and evaluates optional parameter freshness.</summary>
    /// <param name="ownerProcessIds">Ignored because vrcosc is automatically gated by VRChat.exe.</param>
    /// <param name="cancellationToken">Cancels discovery settling and HTTP requests.</param>
    /// <returns>The structural or parameter-level OSCQuery health result.</returns>
    public async Task<HealthProbeResult> CheckAsync(
        IReadOnlySet<int> ownerProcessIds,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        OSCQueryServiceProfile[] profiles = await DiscoverProfilesAsync(cancellationToken)
            .ConfigureAwait(false);

        if (profiles.Length == 0)
        {
            Reset();
            return HealthProbeResult.Failure(
                "VRChat's OSCQuery service was not discovered."
            );
        }

        var failures = new List<string>();

        foreach (OSCQueryServiceProfile profile in profiles)
        {
            try
            {
                HealthProbeResult? result = await TryQueryProfileAsync(
                    profile,
                    cancellationToken
                ).ConfigureAwait(false);

                if (result is not null)
                    return result.Value;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{profile.name}: {ex.Message}");
            }
        }

        Reset();
        string detail = failures.Count == 0
            ? "No discovered OSCQuery service identified itself as VRChat."
            : $"VRChat OSCQuery could not be queried: {string.Join("; ", failures)}";
        return HealthProbeResult.Failure(detail);
    }

    /// <summary>Discovers every parameter leaf exposed by VRChat's current avatar.</summary>
    /// <param name="cancellationToken">Cancels discovery settling and HTTP requests.</param>
    /// <returns>Unique parameter leaf names sorted without case sensitivity.</returns>
    internal async Task<IReadOnlyList<string>> DiscoverParameterLeafNamesAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        OSCQueryServiceProfile[] profiles = await DiscoverProfilesAsync(cancellationToken)
            .ConfigureAwait(false);

        if (profiles.Length == 0)
        {
            throw new InvalidOperationException(
                "VRChat's OSCQuery service was not discovered."
            );
        }

        var failures = new List<string>();

        foreach (OSCQueryServiceProfile profile in profiles)
        {
            try
            {
                if (!await IsVrChatProfileAsync(profile, cancellationToken)
                    .ConfigureAwait(false))
                {
                    continue;
                }

                string json = await _httpClient.GetStringAsync(
                    BuildUri(profile.address, profile.port, "/avatar/parameters"),
                    cancellationToken
                ).ConfigureAwait(false);
                using JsonDocument document = JsonDocument.Parse(json);
                return CollectParameterLeafNames(document.RootElement);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{profile.name}: {ex.Message}");
            }
        }

        string detail = failures.Count == 0
            ? "No discovered OSCQuery service identified itself as VRChat."
            : $"VRChat OSCQuery could not be queried: {string.Join("; ", failures)}";
        throw new InvalidOperationException(detail);
    }

    /// <summary>Clears parameter history so inactive or unreachable time cannot count toward staleness.</summary>
    public void Reset()
    {
        _parameterStates.Clear();
    }

    /// <summary>Stops mDNS discovery, releases HTTP resources, and clears retained parameter samples.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _oscQueryService?.Dispose();
        _oscQueryService = null;
        _httpClient.Dispose();
        Reset();
    }

    /// <summary>Lazily creates a discovery-only OSCQuery service that neither hosts nor advertises AppSupervisor.</summary>
    /// <returns>The reusable OSCQuery discovery service.</returns>
    private OSCQueryService GetOrCreateDiscoveryService()
    {
        return _oscQueryService ??= new OSCQueryServiceBuilder()
            .WithDiscovery(new MeaModDiscovery())
            .Build();
    }

    /// <summary>Refreshes mDNS discovery and returns the currently visible OSCQuery HTTP services.</summary>
    private async Task<OSCQueryServiceProfile[]> DiscoverProfilesAsync(
        CancellationToken cancellationToken)
    {
        OSCQueryService service = GetOrCreateDiscoveryService();
        service.RefreshServices();
        await Task.Delay(DiscoverySettleMilliseconds, cancellationToken)
            .ConfigureAwait(false);

        return service
            .GetOSCQueryServices()
            .Where(profile => profile.serviceType ==
                OSCQueryServiceProfile.ServiceType.OSCQuery)
            .ToArray();
    }

    /// <summary>Identifies one discovered profile through HOST_INFO and evaluates its root or parameter structure.</summary>
    /// <param name="profile">A discovered OSCQuery HTTP service.</param>
    /// <param name="cancellationToken">Cancels HTTP requests.</param>
    /// <returns>A result for VRChat, or <see langword="null"/> when the profile belongs to another application.</returns>
    private async Task<HealthProbeResult?> TryQueryProfileAsync(
        OSCQueryServiceProfile profile,
        CancellationToken cancellationToken)
    {
        if (!await IsVrChatProfileAsync(profile, cancellationToken).ConfigureAwait(false))
            return null;

        if (_parameters.Count == 0)
            return await CheckRootStructureAsync(profile, cancellationToken).ConfigureAwait(false);

        return await CheckParametersAsync(profile, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Identifies whether one OSCQuery profile belongs to VRChat.</summary>
    private async Task<bool> IsVrChatProfileAsync(
        OSCQueryServiceProfile profile,
        CancellationToken cancellationToken)
    {
        Uri hostInfoUri = BuildUri(profile.address, profile.port, "/?HOST_INFO");
        string hostInfoJson = await _httpClient.GetStringAsync(
            hostInfoUri,
            cancellationToken
        ).ConfigureAwait(false);
        using JsonDocument hostInfo = JsonDocument.Parse(hostInfoJson);

        string hostName = ReadStringProperty(hostInfo.RootElement, "NAME") ?? "";
        return profile.name.Contains("VRChat", StringComparison.OrdinalIgnoreCase) ||
            hostName.Contains("VRChat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Validates that VRChat serves a syntactically valid OSCQuery root node.</summary>
    /// <param name="profile">The confirmed VRChat OSCQuery profile.</param>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    /// <returns>A healthy result when the root contains OSCQuery node metadata.</returns>
    private async Task<HealthProbeResult> CheckRootStructureAsync(
        OSCQueryServiceProfile profile,
        CancellationToken cancellationToken)
    {
        string json = await _httpClient.GetStringAsync(
            BuildUri(profile.address, profile.port, "/"),
            cancellationToken
        ).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        bool valid = root.ValueKind == JsonValueKind.Object &&
            (root.TryGetProperty("FULL_PATH", out _) ||
             root.TryGetProperty("CONTENTS", out _));

        return valid
            ? HealthProbeResult.Success("VRChat's OSCQuery address structure is available.")
            : HealthProbeResult.Failure("VRChat returned an invalid OSCQuery root node.");
    }

    /// <summary>Queries the avatar parameter branch and optionally detects a majority of unchanged values.</summary>
    /// <param name="profile">The confirmed VRChat OSCQuery profile.</param>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    /// <returns>A result describing parameter presence or freshness.</returns>
    private async Task<HealthProbeResult> CheckParametersAsync(
        OSCQueryServiceProfile profile,
        CancellationToken cancellationToken)
    {
        string json = await _httpClient.GetStringAsync(
            BuildUri(profile.address, profile.port, "/avatar/parameters"),
            cancellationToken
        ).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(json);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CollectParameterValues(document.RootElement, "", values);

        string[] requestedValues = _parameters
            .Where(values.ContainsKey)
            .ToArray();

        if (requestedValues.Length == 0)
        {
            Reset();
            return HealthProbeResult.Failure(
                "None of the configured VRChat OSC parameters are present on the current avatar."
            );
        }

        if (_stalePeriod is null)
        {
            return HealthProbeResult.Success(
                $"Queried {requestedValues.Length} configured VRChat OSC parameter(s)."
            );
        }

        DateTime nowUtc = SupervisorTime.UtcNow;
        int staleCount = 0;

        foreach (string parameter in requestedValues)
        {
            string rawValue = values[parameter];

            if (_parameterStates.TryGetValue(parameter, out ParameterState previous) &&
                string.Equals(previous.RawValue, rawValue, StringComparison.Ordinal))
            {
                if (nowUtc - previous.UnchangedSinceUtc >= _stalePeriod.Value)
                    staleCount++;
            }
            else
            {
                _parameterStates[parameter] = new ParameterState(rawValue, nowUtc);
            }
        }

        foreach (string missing in _parameterStates.Keys
            .Where(key => !requestedValues.Contains(key, StringComparer.OrdinalIgnoreCase))
            .ToArray())
        {
            _parameterStates.Remove(missing);
        }

        int requiredStale = GetStrictMajorityCount(requestedValues.Length);

        if (staleCount >= requiredStale)
        {
            return HealthProbeResult.Failure(
                $"{staleCount} of {requestedValues.Length} VRChat OSC parameters have not changed for at least {_stalePeriod.Value.TotalSeconds:0} seconds."
            );
        }

        return HealthProbeResult.Success(
            $"VRChat OSC parameter data is available and not majority-stale ({requestedValues.Length} parameter(s))."
        );
    }

    /// <summary>Calculates the smallest count that is strictly greater than half of the available values.</summary>
    /// <param name="availableValueCount">The positive number of parameter values available for comparison.</param>
    /// <returns>The number of stale values required to constitute a strict majority.</returns>
    internal static int GetStrictMajorityCount(int availableValueCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(availableValueCount);
        return (availableValueCount / 2) + 1;
    }

    /// <summary>Collects the unique VALUE leaf names exposed by one OSCQuery branch.</summary>
    internal static IReadOnlyList<string> CollectParameterLeafNames(JsonElement root)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CollectParameterValues(root, "", values);
        return values.Keys
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Builds an HTTP URI for IPv4 or IPv6 OSCQuery profiles.</summary>
    /// <param name="address">The discovered service address.</param>
    /// <param name="port">The discovered HTTP port.</param>
    /// <param name="pathAndQuery">The OSCQuery path and optional query string.</param>
    /// <returns>A correctly escaped HTTP URI.</returns>
    private static Uri BuildUri(IPAddress address, int port, string pathAndQuery)
    {
        return OscQueryUriBuilder.Build(address, port, pathAndQuery);
    }

    /// <summary>Reads a string property without making OSCQuery host-info property casing significant.</summary>
    /// <param name="element">The host-info JSON object.</param>
    /// <param name="propertyName">The OSCQuery property name.</param>
    /// <returns>The string value, or <see langword="null"/> when absent.</returns>
    private static string? ReadStringProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    /// <summary>Walks an OSCQuery branch and records VALUE leaves by their final path segment.</summary>
    /// <param name="node">The current OSCQuery node.</param>
    /// <param name="path">The path accumulated from parent CONTENTS names.</param>
    /// <param name="values">The destination mapping of leaf name to raw JSON value.</param>
    private static void CollectParameterValues(
        JsonElement node,
        string path,
        IDictionary<string, string> values)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return;

        if (node.TryGetProperty("CONTENTS", out JsonElement contents) &&
            contents.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty child in contents.EnumerateObject())
                CollectParameterValues(child.Value, $"{path}/{child.Name}", values);

            return;
        }

        if (!node.TryGetProperty("VALUE", out JsonElement value))
            return;

        int separator = path.LastIndexOf('/');
        string leafName = separator >= 0 ? path[(separator + 1)..] : path;

        if (!string.IsNullOrWhiteSpace(leafName))
            values[leafName] = value.GetRawText();
    }

    /// <summary>Stores the last raw parameter value and the start of its current unchanged streak.</summary>
    private readonly record struct ParameterState(
        string RawValue,
        DateTime UnchangedSinceUtc);
}
