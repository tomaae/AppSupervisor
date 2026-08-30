namespace AppSupervisor.Bluetooth;

/// <summary>Maps received signal strength to deliberately broad proximity bands.</summary>
internal static class BluetoothProximityEstimator
{
    internal static string Format(short? signalStrengthDbm) => signalStrengthDbm switch
    {
        >= -55 => "Very near",
        >= -70 => "Near",
        >= -85 => "Far",
        not null => "Very far",
        null => "Unknown"
    };
}
