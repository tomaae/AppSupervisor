using AppSupervisor.HomeAssistant;

namespace AppSupervisor.Tests;

/// <summary>Verifies Home Assistant REST service payload construction.</summary>
public sealed class HomeAssistantClientPayloadTests
{
    /// <summary>Adds brightness_pct as a numeric field when light brightness is configured.</summary>
    [Fact]
    public void CreateServicePayload_WithBrightness_AddsPercentage()
    {
        IReadOnlyDictionary<string, object> payload =
            HomeAssistantClient.CreateServicePayload("light.test", 42);

        Assert.Equal("light.test", payload["entity_id"]);
        Assert.Equal(42, payload["brightness_pct"]);
    }

    /// <summary>Leaves unrelated service calls free of brightness data.</summary>
    [Fact]
    public void CreateServicePayload_WithoutBrightness_OmitsPercentage()
    {
        IReadOnlyDictionary<string, object> payload =
            HomeAssistantClient.CreateServicePayload("switch.test", null);

        Assert.Equal("switch.test", payload["entity_id"]);
        Assert.DoesNotContain("brightness_pct", payload.Keys);
    }

    /// <summary>Rejects brightness values outside AppSupervisor's supported on-state range.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void CreateServicePayload_InvalidBrightness_Throws(int brightnessPercent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HomeAssistantClient.CreateServicePayload("light.test", brightnessPercent));
    }
}
