using System.Text.Json;
using AppSupervisor.HomeAssistant;

namespace AppSupervisor.Tests;

/// <summary>Verifies Home Assistant entity-state parsing used by verification and persistence.</summary>
public sealed class HomeAssistantClientStateTests
{
    [Theory]
    [InlineData(89, 35)]
    [InlineData(179, 70)]
    [InlineData(255, 100)]
    public void ReadEntityState_NormalizesRawBrightnessToPercentage(
        int rawBrightness,
        int expectedPercentage)
    {
        using JsonDocument document = JsonDocument.Parse(
            $$"""
            {
              "state": "on",
              "attributes": {
                "brightness": {{rawBrightness}}
              }
            }
            """
        );

        HomeAssistantEntityState state = HomeAssistantClient.ReadEntityState(
            document.RootElement
        );

        Assert.Equal("on", state.State);
        Assert.Equal(expectedPercentage, state.BrightnessPercent);
    }

    [Fact]
    public void ReadEntityState_MissingBrightnessLeavesPercentageUnknown()
    {
        using JsonDocument document = JsonDocument.Parse(
            """{"state":"off","attributes":{}}"""
        );

        HomeAssistantEntityState state = HomeAssistantClient.ReadEntityState(
            document.RootElement
        );

        Assert.Equal("off", state.State);
        Assert.Null(state.BrightnessPercent);
    }

    [Fact]
    public void ReadEntityState_NullBrightnessLeavesPercentageUnknown()
    {
        using JsonDocument document = JsonDocument.Parse(
            """{"state":"off","attributes":{"brightness":null}}"""
        );

        HomeAssistantEntityState state = HomeAssistantClient.ReadEntityState(
            document.RootElement
        );

        Assert.Equal("off", state.State);
        Assert.Null(state.BrightnessPercent);
    }
}
