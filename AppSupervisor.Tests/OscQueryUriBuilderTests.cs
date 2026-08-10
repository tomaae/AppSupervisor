using System.Net;
using AppSupervisor.Health;

namespace AppSupervisor.Tests;

/// <summary>
/// Verifies OSCQuery discovery endpoints are converted into valid HTTP paths and query strings.
/// </summary>
public sealed class OscQueryUriBuilderTests
{
    /// <summary>Confirms HOST_INFO remains a query instead of being escaped into the root path.</summary>
    [Fact]
    public void Build_HostInfoQuery_PreservesQueryString()
    {
        Uri uri = OscQueryUriBuilder.Build(
            IPAddress.Loopback,
            12345,
            "/?HOST_INFO"
        );

        Assert.Equal("/", uri.AbsolutePath);
        Assert.Equal("?HOST_INFO", uri.Query);
    }

    /// <summary>Confirms ordinary OSCQuery node paths remain unchanged.</summary>
    [Fact]
    public void Build_ParameterPath_PreservesPath()
    {
        Uri uri = OscQueryUriBuilder.Build(
            IPAddress.IPv6Loopback,
            12345,
            "/avatar/parameters"
        );

        Assert.Equal("/avatar/parameters", uri.AbsolutePath);
        Assert.Equal("", uri.Query);
    }
}
