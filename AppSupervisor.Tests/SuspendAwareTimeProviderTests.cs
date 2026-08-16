using AppSupervisor.Core;

namespace AppSupervisor.Tests;

public sealed class SuspendAwareTimeProviderTests
{
    [Fact]
    public void Resume_ExcludesSuspendedDurationFromSupervisorTime()
    {
        var inner = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var time = new SuspendAwareTimeProvider(inner);
        inner.Advance(TimeSpan.FromSeconds(5));

        time.Suspend();
        inner.Advance(TimeSpan.FromHours(2));

        Assert.Equal(TimeSpan.FromSeconds(5), time.GetUtcNow() - inner.Origin);
        time.Resume();
        inner.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(7), time.GetUtcNow() - inner.Origin);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset origin)
        {
            Origin = origin;
            _utcNow = origin;
        }

        public DateTimeOffset Origin { get; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan amount)
        {
            _utcNow += amount;
            _timestamp += amount.Ticks;
        }
    }
}
