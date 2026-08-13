using AppSupervisor.Health;

namespace AppSupervisor.Tests;

/// <summary>Verifies helper-window responsiveness aggregation without calling the native desktop APIs.</summary>
public sealed class ApplicationResponsivenessProbeTests
{
    /// <summary>Confirms helpers without an owned top-level window are not falsely classified as frozen.</summary>
    [Fact]
    public async Task CheckAsync_NoOwnedWindows_ReturnsHealthyNotMeasurableResult()
    {
        using var probe = new ApplicationResponsivenessProbe(
            _ => [],
            _ => throw new InvalidOperationException("No window should be probed.")
        );

        HealthProbeResult result = await probe.CheckAsync(
            new HashSet<int> { 10 },
            CancellationToken.None
        );

        Assert.True(result.Healthy);
        Assert.Contains("cannot be measured", result.Detail);
    }

    /// <summary>Confirms one stalled internal window cannot outweigh a responsive helper UI.</summary>
    [Fact]
    public async Task CheckAsync_OneResponsiveWindow_ReturnsSuccess()
    {
        IntPtr responsiveWindow = new(1);
        IntPtr hungWindow = new(2);
        using var probe = new ApplicationResponsivenessProbe(
            _ => [responsiveWindow, hungWindow],
            window => window != hungWindow
        );

        HealthProbeResult result = await probe.CheckAsync(
            new HashSet<int> { 10 },
            CancellationToken.None
        );

        Assert.True(result.Healthy);
        Assert.Equal("1 of 2 helper windows are responding.", result.Detail);
    }

    /// <summary>Confirms a helper is frozen when none of its discoverable windows process messages.</summary>
    [Fact]
    public async Task CheckAsync_NoResponsiveWindows_ReturnsFailure()
    {
        using var probe = new ApplicationResponsivenessProbe(
            _ => [new IntPtr(1), new IntPtr(2)],
            _ => false
        );

        HealthProbeResult result = await probe.CheckAsync(
            new HashSet<int> { 10 },
            CancellationToken.None
        );

        Assert.False(result.Healthy);
        Assert.Equal("None of the 2 helper windows are responding.", result.Detail);
    }

    /// <summary>Confirms multiple responsive helper windows produce a healthy aggregate result.</summary>
    [Fact]
    public async Task CheckAsync_AllWindowsRespond_ReturnsSuccess()
    {
        using var probe = new ApplicationResponsivenessProbe(
            _ => [new IntPtr(1), new IntPtr(2)],
            _ => true
        );

        HealthProbeResult result = await probe.CheckAsync(
            new HashSet<int> { 10 },
            CancellationToken.None
        );

        Assert.True(result.Healthy);
        Assert.Equal("All 2 helper windows are responding.", result.Detail);
    }

    /// <summary>Confirms helper responsiveness monitoring remains opt-in for new configuration objects.</summary>
    [Fact]
    public void ManagedApplicationConfig_DefaultsResponsivenessMonitoringOff()
    {
        var config = new ManagedApplicationConfig();

        Assert.False(config.MonitorResponsiveness);
    }
}
