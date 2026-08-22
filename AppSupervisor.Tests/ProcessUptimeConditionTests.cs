using AppSupervisor.Configuration;
using AppSupervisor.Health;

namespace AppSupervisor.Tests;

/// <summary>Verifies the continuous VRChat uptime prerequisite used by VRCOSC checks.</summary>
public sealed class ProcessUptimeConditionTests
{
    /// <summary>Confirms VRCOSC performs no probe before VRChat reaches three minutes of uptime.</summary>
    [Fact]
    public void Poll_VrcOscBeforeMinimumVrChatUptime_SkipsProbe()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero)
        );
        DateTime processStartUtc = time.GetUtcNow().UtcDateTime;
        var probe = new CountingProbe();
        var condition = new ProcessUptimeCondition(
            "VRChat.exe",
            HealthCheckFactory.VrcOscMinimumVrChatUptime,
            _ => [42],
            _ => processStartUtc,
            time
        );
        using var check = new ManagedHealthCheck(
            new HealthCheckConfig
            {
                Name = "VRCOSC",
                Type = HealthCheckType.Vrcosc,
                IntervalSeconds = 10,
                TimeoutSeconds = 3,
                FailureThreshold = 3,
                StartupDelaySeconds = 10
            },
            probe,
            condition
        );
        IReadOnlySet<int> helperProcessIds = new HashSet<int> { 7 };

        check.Poll(helperProcessIds, time.GetUtcNow().UtcDateTime);
        time.Advance(TimeSpan.FromMinutes(3) - TimeSpan.FromMilliseconds(1));
        check.Poll(helperProcessIds, time.GetUtcNow().UtcDateTime);

        Assert.Equal(0, probe.CallCount);

        time.Advance(TimeSpan.FromMilliseconds(1));
        check.Poll(helperProcessIds, time.GetUtcNow().UtcDateTime);

        Assert.Equal(1, probe.CallCount);
    }

    /// <summary>Confirms replacing VRChat with a new process resets the qualifying uptime.</summary>
    [Fact]
    public void IsActive_VrChatRestarts_RequiresNewProcessToMature()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero)
        );
        IReadOnlyList<int> processIds = [42];
        var startTimes = new Dictionary<int, DateTime>
        {
            [42] = time.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(3)
        };
        var condition = new ProcessUptimeCondition(
            "VRChat.exe",
            TimeSpan.FromMinutes(3),
            _ => processIds,
            processId => startTimes.TryGetValue(processId, out DateTime startTime)
                ? startTime
                : null,
            time
        );

        Assert.True(condition.IsActive());

        processIds = [84];
        startTimes[84] = time.GetUtcNow().UtcDateTime;

        Assert.False(condition.IsActive());

        time.Advance(TimeSpan.FromMinutes(3));

        Assert.True(condition.IsActive());
    }

    /// <summary>Confirms the VRCOSC factory selects the uptime gate rather than a presence-only gate.</summary>
    [Fact]
    public void CreateActivationCondition_VrcOsc_UsesThreeMinuteUptimeGate()
    {
        IHealthCheckActivationCondition condition =
            HealthCheckFactory.CreateActivationCondition(new HealthCheckConfig
            {
                Type = HealthCheckType.Vrcosc
            });

        Assert.IsType<ProcessUptimeCondition>(condition);
        Assert.Equal(TimeSpan.FromMinutes(3), HealthCheckFactory.VrcOscMinimumVrChatUptime);
    }

    /// <summary>Confirms an explicit editor test still runs whenever VRChat is currently present.</summary>
    [Fact]
    public void CreateOneShotActivationCondition_VrcOsc_UsesPresenceOnlyGate()
    {
        IHealthCheckActivationCondition condition =
            HealthCheckFactory.CreateOneShotActivationCondition(new HealthCheckConfig
            {
                Type = HealthCheckType.Vrcosc
            });

        Assert.IsType<ProcessRunningCondition>(condition);
    }

    /// <summary>Records whether the health-check state machine actually invoked the probe.</summary>
    private sealed class CountingProbe : IHealthProbe
    {
        public int CallCount { get; private set; }

        public Task<HealthProbeResult> CheckAsync(
            IReadOnlySet<int> ownerProcessIds,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(HealthProbeResult.Success());
        }

        public void Reset()
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Provides deterministic wall-clock time for process-uptime checks.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount)
        {
            _utcNow += amount;
        }
    }
}
