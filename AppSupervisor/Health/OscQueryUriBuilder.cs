using System.Net;

namespace AppSupervisor.Health;

/// <summary>
/// Builds OSCQuery HTTP URIs while preserving query strings such as ?HOST_INFO.
/// </summary>
internal static class OscQueryUriBuilder
{
    /// <summary>Builds an IPv4- or IPv6-safe URI from a discovered endpoint and OSCQuery path.</summary>
    /// <param name="address">The discovered HTTP address.</param>
    /// <param name="port">The discovered HTTP port.</param>
    /// <param name="pathAndQuery">The OSCQuery path and optional query string.</param>
    /// <returns>A URI with query data kept separate from the escaped path.</returns>
    public static Uri Build(IPAddress address, int port, string pathAndQuery)
    {
        int querySeparator = pathAndQuery.IndexOf('?');
        string path = querySeparator >= 0
            ? pathAndQuery[..querySeparator]
            : pathAndQuery;
        var builder = new UriBuilder("http", address.ToString(), port, path);

        if (querySeparator >= 0)
            builder.Query = pathAndQuery[(querySeparator + 1)..];

        return builder.Uri;
    }
}
