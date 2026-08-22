using AppSupervisor.Health;
using System.Text.Json;

namespace AppSupervisor.Tests;

/// <summary>
/// Verifies VRChat OSCQuery parameter-freshness threshold calculations.
/// </summary>
public sealed class VrcOscQueryProbeTests
{
    /// <summary>Confirms nested OSCQuery VALUE nodes become unique sorted leaf names.</summary>
    [Fact]
    public void CollectParameterLeafNames_NestedAddressTree_ReturnsValueLeaves()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "CONTENTS": {
                "JawOpen": { "VALUE": [0.25] },
                "Face": {
                  "CONTENTS": {
                    "EyeLeft": { "VALUE": [0.5] },
                    "MetadataOnly": { "TYPE": "f" }
                  }
                }
              }
            }
            """
        );

        IReadOnlyList<string> parameters =
            VrcOscQueryProbe.CollectParameterLeafNames(document.RootElement);

        Assert.Equal(["EyeLeft", "JawOpen"], parameters);
    }

    /// <summary>Confirms stale values must be strictly more than half of all available values.</summary>
    /// <param name="availableValues">The available parameter count.</param>
    /// <param name="expectedMajority">The smallest strict majority.</param>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(8, 5)]
    public void GetStrictMajorityCount_AvailableValues_ReturnsStrictMajority(
        int availableValues,
        int expectedMajority)
    {
        Assert.Equal(
            expectedMajority,
            VrcOscQueryProbe.GetStrictMajorityCount(availableValues)
        );
    }

    /// <summary>Confirms an empty parameter set is rejected instead of producing an unusable threshold.</summary>
    [Fact]
    public void GetStrictMajorityCount_NoAvailableValues_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VrcOscQueryProbe.GetStrictMajorityCount(0)
        );
    }
}
