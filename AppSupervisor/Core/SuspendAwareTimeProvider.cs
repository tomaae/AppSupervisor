namespace AppSupervisor.Core;

/// <summary>
/// Supplies monotonic supervisor time while excluding periods in which Windows is suspended.
/// </summary>
internal sealed class SuspendAwareTimeProvider : TimeProvider
{
    private readonly object _syncRoot = new();
    private readonly TimeProvider _inner;
    private readonly DateTimeOffset _originUtc;
    private readonly long _originTimestamp;
    private TimeSpan _suspendedDuration;
    private long? _suspendedAtTimestamp;

    public SuspendAwareTimeProvider(TimeProvider inner)
    {
        _inner = inner;
        _originUtc = inner.GetUtcNow();
        _originTimestamp = inner.GetTimestamp();
    }

    /// <summary>Gets monotonic UTC-like time whose elapsed duration excludes system suspension.</summary>
    public override DateTimeOffset GetUtcNow()
    {
        lock (_syncRoot)
        {
            long timestamp = _suspendedAtTimestamp ?? _inner.GetTimestamp();
            return _originUtc +
                _inner.GetElapsedTime(_originTimestamp, timestamp) -
                _suspendedDuration;
        }
    }

    /// <summary>Freezes supervisor time at the beginning of a Windows suspend transition.</summary>
    public void Suspend()
    {
        lock (_syncRoot)
            _suspendedAtTimestamp ??= _inner.GetTimestamp();
    }

    /// <summary>Resumes supervisor time without counting the suspended interval.</summary>
    public void Resume()
    {
        lock (_syncRoot)
        {
            if (_suspendedAtTimestamp is not long suspendedAt)
                return;

            long resumedAt = _inner.GetTimestamp();
            _suspendedDuration += _inner.GetElapsedTime(suspendedAt, resumedAt);
            _suspendedAtTimestamp = null;
        }
    }
}

/// <summary>Owns the process-wide time source used by recurring supervision state machines.</summary>
internal static class SupervisorTime
{
    private static readonly SuspendAwareTimeProvider Clock = new(TimeProvider.System);

    public static TimeProvider Provider => Clock;

    public static DateTime UtcNow => Clock.GetUtcNow().UtcDateTime;

    public static void Suspend() => Clock.Suspend();

    public static void Resume() => Clock.Resume();
}
