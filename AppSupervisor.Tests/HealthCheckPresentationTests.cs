using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;

namespace AppSupervisor.Tests;

/// <summary>Verifies health-check names rely on icons instead of redundant type suffixes.</summary>
public sealed class HealthCheckPresentationTests
{
    [Fact]
    public void ListItem_AllTypes_ReturnsConciseNameAndState()
    {
        var listener = new HealthCheckConfig
        {
            Name = "Web listener",
            Type = HealthCheckType.Listener
        };
        var oscQuery = new HealthCheckConfig
        {
            Name = "Face tracking",
            Type = HealthCheckType.Vrcosc,
            Enabled = false
        };

        Assert.Equal("Web listener", HealthCheckDisplay.ListItem(listener));
        Assert.Equal("Face tracking (disabled)", HealthCheckDisplay.ListItem(oscQuery));
        Assert.DoesNotContain('[', HealthCheckDisplay.ListItem(listener));
        Assert.DoesNotContain('[', HealthCheckDisplay.ListItem(oscQuery));
    }
}
