using AppSupervisor.Bluetooth;

namespace AppSupervisor.Tests;

/// <summary>Verifies broad Bluetooth signal-strength proximity estimates.</summary>
public sealed class BluetoothProximityTests
{
    [Theory]
    [InlineData(-40, "Very near")]
    [InlineData(-55, "Very near")]
    [InlineData(-56, "Near")]
    [InlineData(-70, "Near")]
    [InlineData(-71, "Far")]
    [InlineData(-85, "Far")]
    [InlineData(-86, "Very far")]
    [InlineData(-110, "Very far")]
    public void Format_ValidSignal_ReturnsExpectedBand(short signal, string expected)
    {
        Assert.Equal(expected, BluetoothProximityEstimator.Format(signal));
    }

    [Fact]
    public void Format_MissingSignal_ReturnsUnknown()
    {
        Assert.Equal("Unknown", BluetoothProximityEstimator.Format(null));
    }

    [Fact]
    public void SelectStrongestSignal_ReturnsLeastNegativeObservation()
    {
        Assert.Null(BluetoothDeviceScanner.SelectStrongestSignal(null, null));
        Assert.Equal((short)-80, BluetoothDeviceScanner.SelectStrongestSignal(-80, null));
        Assert.Equal((short)-65, BluetoothDeviceScanner.SelectStrongestSignal(null, -65));
        Assert.Equal((short)-65, BluetoothDeviceScanner.SelectStrongestSignal(-80, -65));
        Assert.Equal((short)-50, BluetoothDeviceScanner.SelectStrongestSignal(-50, -75));
    }
}
